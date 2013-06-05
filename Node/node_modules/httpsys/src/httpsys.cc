#include "httpsys.h"

/*
Design notes:
- Only one async operation per HTTP request should be outstanding at a time. JavaScript 
  must ensure not to initiate another async operation (e.g. httpsys_write_body) before 
  the ongoing one completes. This implies JavaScript must manage a state machine around a request
  and buffer certain calls from user code (e.g. writing multiple chunks of response body before
  previous write completes)
- Native resources are released by native code if async operation completes with error.
- If JavaScript encounters an error it must explicitly request native resources to be released.
  In particular there is no exception contract between JavaScript callback and native code.
- JavaScript cannot make any additional calls into native in the context of a particular request
  after it has been called with an error event type; at that time all native resources had already 
  been cleaned up.
*/

using namespace v8;

int initialized;
int initialBufferSize;
ULONG requestQueueLength;
int pendingReadCount;
Persistent<Function> callback;
Persistent<Function> bufferConstructor;
HTTP_CACHE_POLICY cachePolicy;
ULONG defaultCacheDuration;
Persistent<ObjectTemplate> httpsysObject;

// Global V8 strings reused across requests
Handle<String> v8uv_httpsys_server;
Handle<String> v8method;
Handle<String> v8req;
Handle<String> v8httpHeaders;
Handle<String> v8httpVersionMajor;
Handle<String> v8httpVersionMinor;
Handle<String> v8eventType;
Handle<String> v8code;
Handle<String> v8url;
Handle<String> v8uv_httpsys;
Handle<String> v8data;
Handle<String> v8statusCode;
Handle<String> v8reason;
Handle<String> v8knownHeaders;
Handle<String> v8unknownHeaders;
Handle<String> v8isLastChunk;
Handle<String> v8chunks;
Handle<String> v8id;
Handle<String> v8value;
Handle<String> v8cacheDuration;

// Maps HTTP_HEADER_ID enum to v8 string
// http://msdn.microsoft.com/en-us/library/windows/desktop/aa364526(v=vs.85).aspx
Handle<String> v8httpRequestHeaderNames[HttpHeaderRequestMaximum];
char* requestHeaders[] = {
    "cache-control",
    "connection",
    "date",
    "keep-alive",
    "pragma",
    "trailer",
    "transfer-encoding",
    "upgrade",
    "via",
    "warning",
    "alive",
    "content-length",
    "content-type",
    "content-encoding",
    "content-language",
    "content-location",
    "content-md5",
    "content-range",
    "expires",
    "last-modified",
    "accept",
    "accept-charset",
    "accept-encoding",
    "accept-language",
    "authorization",
    "cookie",
    "expect",
    "from",
    "host",
    "if-match",
    "if-modified-since",
    "if-none-match",
    "if-range",
    "if-unmodified-since",
    "max-forwards",
    "proxy-authorization",
    "referer",
    "range",
    "te",
    "translate",
    "user-agent"
};

// Maps HTTP_VERB enum to V8 string
// http://msdn.microsoft.com/en-us/library/windows/desktop/aa364664(v=vs.85).aspx
Handle<String> v8verbs[HttpVerbMaximum];
char* verbs[] = {
    NULL,
    NULL,
    NULL,
    "OPTIONS",
    "GET",
    "HEAD",
    "POST",
    "PUT",
    "DETELE",
    "TRACE",
    "CONNECT",
    "TRACK",
    "MOVE",
    "COPY",
    "PROPFIND",
    "PROPPATCH",
    "MKCOL",
    "LOCK",
    "UNLOCK",
    "SEARCH"
};

// Processing common to all callbacks from HTTP.SYS:
// - map the uv_async_t handle to uv_httpsys_t
// - decrement event loop reference count to indicate completion of async operation
#define HTTPSYS_CALLBACK_PREAMBLE \
    HandleScope handleScope; \
    uv_httpsys_t* uv_httpsys = CONTAINING_RECORD(handle, uv_httpsys_t, uv_async); \
    uv_unref((uv_handle_t*)&uv_httpsys->uv_async); \
    uv_httpsys->uv_async.loop = NULL; \
    PHTTP_REQUEST request = (PHTTP_REQUEST)uv_httpsys->buffer; 

// Processing common to most exported methods:
// - declare handle scope and hr
// - extract uv_httpsys_t from the internal field of the object passed as the first parameter
#define HTTPSYS_EXPORT_PREAMBLE \
    HandleScope handleScope; \
    HRESULT hr; \
    uv_httpsys_t* uv_httpsys = (uv_httpsys_t*)Handle<Object>::Cast(args[0])->GetPointerFromInternalField(0);

Handle<Value> httpsys_make_callback(Handle<Value> options)
{
    HandleScope handleScope;
    Handle<Value> argv[] = { options };

    TryCatch try_catch;

    Handle<Value> result = callback->Call(Context::GetCurrent()->Global(), 1, argv);

    if (try_catch.HasCaught()) {
        node::FatalException(try_catch);
    }

    return handleScope.Close(result);
}

Handle<Object> httpsys_create_event(uv_httpsys_server_t* uv_httpsys_server, int eventType)
{
    HandleScope handleScope;

    uv_httpsys_server->event->Set(v8eventType, Integer::NewFromUnsigned(eventType));

    return uv_httpsys_server->event;
}

Handle<Object> httpsys_create_event(uv_httpsys_t* uv_httpsys, int eventType)
{
    HandleScope handleScope;

    uv_httpsys->event->Set(v8eventType, Integer::NewFromUnsigned(eventType));

    return uv_httpsys->event;
}

Handle<Value> httpsys_notify_error(uv_httpsys_server_t* uv_httpsys_server, uv_httpsys_event_type errorType, unsigned int code)
{
    HandleScope handleScope;

    Handle<Object> error = httpsys_create_event(uv_httpsys_server, errorType);
    error->Set(v8code, Integer::NewFromUnsigned(code));

    return handleScope.Close(httpsys_make_callback(error));
}

Handle<Value> httpsys_notify_error(uv_httpsys_t* uv_httpsys, uv_httpsys_event_type errorType, unsigned int code)
{
    HandleScope handleScope;

    Handle<Object> error = httpsys_create_event(uv_httpsys, errorType);
    error->Set(v8code, Integer::NewFromUnsigned(code));

    return handleScope.Close(httpsys_make_callback(error));
}

void httpsys_new_request_callback(uv_async_t* handle, int status)
{
    HTTPSYS_CALLBACK_PREAMBLE

    // Copy the request ID assigned to the request by HTTP.SYS to uv_httpsys 
    // to start subsequent async operations related to this request

    uv_httpsys->requestId = request->RequestId;

    // Increase the count of new read requests to initialize to replace the one that just completed.
    // Actual initialization will be done in the uv_prepare callback httpsys_prepare_new_requests 
    // associated with this server.

    uv_httpsys->uv_httpsys_server->readsToInitialize++;

    // Initialize the JavaScript representation of an event object that will be used
    // to marshall data into JavaScript for the lifetime of this request.

    uv_httpsys->event = Persistent<Object>::New(httpsysObject->NewInstance());
    uv_httpsys->event->SetPointerInInternalField(0, (void*)uv_httpsys);
    uv_httpsys->event->Set(v8uv_httpsys_server, uv_httpsys->uv_httpsys_server->event);     

    // Process async completion

    if (S_OK != (NTSTATUS)uv_httpsys->uv_async.async_req.overlapped.Internal)
    {
        // Async completion failed - notify JavaScript
        httpsys_notify_error(
            uv_httpsys, 
            HTTPSYS_ERROR_NEW_REQUEST,
            (unsigned int)uv_httpsys->uv_async.async_req.overlapped.Internal);
        httpsys_free(uv_httpsys);
        uv_httpsys = NULL;
    }
    else
    {
        // New request received - notify JavaScript

        Handle<Object> event = httpsys_create_event(uv_httpsys, HTTPSYS_NEW_REQUEST);

        // Create the 'req' object representing the request

        Handle<Object> req = Object::New();
        event->Set(v8req, req);

        // Add HTTP verb information

        if (HttpVerbUnknown == request->Verb)
        {
            req->Set(v8method, String::New(request->pUnknownVerb));
        }
        else 
        {
            req->Set(v8method, v8verbs[request->Verb]);
        }

        // Add known HTTP header information

        Handle<Object> headers = Object::New();
        req->Set(v8httpHeaders, headers);

        for (int i = 0; i < HttpHeaderRequestMaximum; i++)
        {
            if (request->Headers.KnownHeaders[i].RawValueLength > 0)
            {
                headers->Set(v8httpRequestHeaderNames[i], String::New(
                    request->Headers.KnownHeaders[i].pRawValue,
                    request->Headers.KnownHeaders[i].RawValueLength));
            }
        }

        // Add custom HTTP header information

        for (int i = 0; i < request->Headers.UnknownHeaderCount; i++)
        {
            // TODO: lowercase unknown header names
            headers->Set(
                String::New(
                    request->Headers.pUnknownHeaders[i].pName,
                    request->Headers.pUnknownHeaders[i].NameLength),
                String::New(
                    request->Headers.pUnknownHeaders[i].pRawValue,
                    request->Headers.pUnknownHeaders[i].RawValueLength));
        }

        // TODO: process trailers

        // Add HTTP version information

        req->Set(v8httpVersionMajor, Integer::NewFromUnsigned(request->Version.MajorVersion));
        req->Set(v8httpVersionMinor, Integer::NewFromUnsigned(request->Version.MinorVersion));

        // Add URL information

        req->Set(v8url, String::New(request->pRawUrl, request->RawUrlLength));

        // Invoke the JavaScript callback passing event as the only paramater

        Handle<Value> result = httpsys_make_callback(event);
        if (result->IsBoolean() && result->BooleanValue())
        {
            // If the callback response is 'true', proceed to process the request body. 
            // Otherwise request had been paused and will be resumed asynchronously from JavaScript
            // with a call to httpsys_resume.

            if (0 == (request->Flags & HTTP_REQUEST_FLAG_MORE_ENTITY_BODY_EXISTS))
            {
                // This is a body-less request. Notify JavaScript the request is finished.

                Handle<Object> event = httpsys_create_event(uv_httpsys, HTTPSYS_END_REQUEST);
                httpsys_make_callback(event);
            }
            else 
            {
                // Start synchronous body reading loop.

                httpsys_read_request_body_loop(uv_httpsys);
            }
        }
    }
}

HRESULT httpsys_initiate_new_request(uv_httpsys_t* uv_httpsys)
{
    HRESULT hr;

    // Create libuv async handle and initialize it

    CheckError(uv_async_init(uv_default_loop(), &uv_httpsys->uv_async, httpsys_new_request_callback));

    // Allocate initial buffer to receice the HTTP request

    uv_httpsys->bufferSize = initialBufferSize;
    ErrorIf(NULL == (uv_httpsys->buffer = malloc(uv_httpsys->bufferSize)), ERROR_NOT_ENOUGH_MEMORY);
    RtlZeroMemory(uv_httpsys->buffer, uv_httpsys->bufferSize);

    // Initiate async receive of a new request with HTTP.SYS, using the OVERLAPPED
    // associated with the default libuv event loop. 

    hr = HttpReceiveHttpRequest(
        uv_httpsys->uv_httpsys_server->requestQueue,
        HTTP_NULL_ID,
        0,
        (PHTTP_REQUEST)uv_httpsys->buffer,
        uv_httpsys->bufferSize,
        NULL,
        &uv_httpsys->uv_async.async_req.overlapped);

    if (NO_ERROR == hr)
    {
        // Synchronous completion.  

        httpsys_new_request_callback(&uv_httpsys->uv_async, 0);
    }
    else 
    {
        ErrorIf(ERROR_IO_PENDING != hr, hr);
    }

    return S_OK;

Error:

    return hr;
}

void httpsys_free_chunks(uv_httpsys_t* uv_httpsys)
{
    if (uv_httpsys->chunk.FromMemory.pBuffer) 
    {
        free(uv_httpsys->chunk.FromMemory.pBuffer);
        RtlZeroMemory(&uv_httpsys->chunk, sizeof(uv_httpsys->chunk));
    }
}

void httpsys_free(uv_httpsys_t* uv_httpsys)
{
    if (NULL != uv_httpsys) 
    {
        httpsys_free_chunks(uv_httpsys);

        if (!uv_httpsys->event.IsEmpty())
        {
            uv_httpsys->event.Dispose();
            uv_httpsys->event.Clear();
        }

        if (uv_httpsys->response.pReason)
        {
            free((void*)uv_httpsys->response.pReason);
        }

        for (int i = 0; i < HttpHeaderResponseMaximum; i++)
        {
            if (uv_httpsys->response.Headers.KnownHeaders[i].pRawValue)
            {
                free((void*)uv_httpsys->response.Headers.KnownHeaders[i].pRawValue);
            }
        }

        if (uv_httpsys->response.Headers.pUnknownHeaders)
        {
            for (int i = 0; i < uv_httpsys->response.Headers.UnknownHeaderCount; i++)
            {
                if (uv_httpsys->response.Headers.pUnknownHeaders[i].pName)
                {
                    free((void*)uv_httpsys->response.Headers.pUnknownHeaders[i].pName);
                }

                if (uv_httpsys->response.Headers.pUnknownHeaders[i].pRawValue)
                {
                    free((void*)uv_httpsys->response.Headers.pUnknownHeaders[i].pRawValue);
                }
            }

            free(uv_httpsys->response.Headers.pUnknownHeaders);
        }

        RtlZeroMemory(&uv_httpsys->response, sizeof (uv_httpsys->response));

        if (NULL != uv_httpsys->uv_async.loop)
        {
            uv_unref((uv_handle_t*)&uv_httpsys->uv_async);
        }

        if (NULL != uv_httpsys->buffer)
        {
            free(uv_httpsys->buffer);
            uv_httpsys->buffer = NULL;
        }

        free(uv_httpsys);
        uv_httpsys = NULL;
    }
}

void httpsys_read_request_body_callback(uv_async_t* handle, int status)
{
    HTTPSYS_CALLBACK_PREAMBLE

    // The "status" parameter is 0 if the callback is an async completion from libuv. 
    // Otherwise, the parameter is a uv_httpsys_t** that the callback is supposed to set to the
    // uv_httpsys that just completed if reading of the body should continue synchronously
    // (i.e. there is no error during the callback and the application does not pause the request),
    // or to NULL if the body should not be read any more.

    uv_httpsys_t** uv_httpsys_result = (uv_httpsys_t**)status;
    if (uv_httpsys_result)
    {
        *uv_httpsys_result = NULL;
    }

    // Process async completion

    if (ERROR_HANDLE_EOF == (NTSTATUS)uv_httpsys->uv_async.async_req.overlapped.Internal)
    {
        // End of request body - notify JavaScript

        Handle<Object> event = httpsys_create_event(uv_httpsys, HTTPSYS_END_REQUEST);
        httpsys_make_callback(event);
    }
    else if (S_OK != (NTSTATUS)uv_httpsys->uv_async.async_req.overlapped.Internal)
    {
        // Async completion failed - notify JavaScript

        httpsys_notify_error(
            uv_httpsys, 
            HTTPSYS_ERROR_READ_REQUEST_BODY, 
            (unsigned int)uv_httpsys->uv_async.async_req.overlapped.Internal);
        httpsys_free(uv_httpsys);
        uv_httpsys = NULL;
    }
    else
    {
        // Successful completion - send body chunk to JavaScript as a Buffer

        // Good explanation of native Buffers at 
        // http://sambro.is-super-awesome.com/2011/03/03/creating-a-proper-buffer-in-a-node-c-addon/

        Handle<Object> event = httpsys_create_event(uv_httpsys, HTTPSYS_REQUEST_BODY);
        ULONG length = (ULONG)uv_httpsys->uv_async.async_req.overlapped.InternalHigh;
        node::Buffer* slowBuffer = node::Buffer::New(length);
        memcpy(node::Buffer::Data(slowBuffer), uv_httpsys->buffer, length);
        Handle<Value> args[] = { slowBuffer->handle_, Integer::New(length), Integer::New(0) };
        Handle<Object> fastBuffer = bufferConstructor->NewInstance(3, args);
        event->Set(v8data, fastBuffer);

        Handle<Value> result = httpsys_make_callback(event);

        if (result->IsBoolean() && result->BooleanValue())
        {
            // If the callback response is 'true', proceed to read more of the request body. 
            // Otherwise request had been paused and will be resumed asynchronously from JavaScript
            // with a call to httpsys_resume.

            if (uv_httpsys_result)
            {
                // This is a synchronous completion of a read. Indicate to the caller the reading should continue
                // and unwind the stack.

                *uv_httpsys_result = uv_httpsys;
            }
            else 
            {
                // This is an asynchronous completion of a read. Restart the reading loop. 

                httpsys_read_request_body_loop(uv_httpsys);
            }       
        }       
    }
}

HRESULT httpsys_read_request_body_loop(uv_httpsys_t* uv_httpsys)
{
    HRESULT hr;

    // Continue reading the request body synchronously until EOF, and error, 
    // request is paused or async oompletion is expected.
    while (NULL != uv_httpsys && NO_ERROR == (hr = httpsys_initiate_read_request_body(uv_httpsys)))
    {
        // Use the "status" parameter to the callback as a mechanism to return data
        // from the callback. If upon return the uv_httpsys is still not NULL,
        // it means there was no error and the request was not paused by the application.

        httpsys_read_request_body_callback(&uv_httpsys->uv_async, (int)&uv_httpsys);
    }

    return (NO_ERROR == hr || ERROR_HANDLE_EOF == hr || ERROR_IO_PENDING == hr) ? S_OK : hr;
}

HRESULT httpsys_initiate_read_request_body(uv_httpsys_t* uv_httpsys)
{
    HandleScope handleScope;
    HRESULT hr;

    // Initialize libuv handle representing this async operation

    RtlZeroMemory(&uv_httpsys->uv_async, sizeof(uv_async_t));
    CheckError(uv_async_init(uv_default_loop(), &uv_httpsys->uv_async, httpsys_read_request_body_callback));

    // Initiate async receive of the HTTP request body

    hr = HttpReceiveRequestEntityBody(
        uv_httpsys->uv_httpsys_server->requestQueue,
        uv_httpsys->requestId,
        0,  
        uv_httpsys->buffer,
        uv_httpsys->bufferSize,
        NULL,
        &uv_httpsys->uv_async.async_req.overlapped);

    if (ERROR_HANDLE_EOF == hr)
    {
        // End of request body, decrement libuv loop ref count since no async completion will follow
        // and generate JavaScript event
        
        uv_unref((uv_handle_t*)&uv_httpsys->uv_async);
        uv_httpsys->uv_async.loop = NULL;
        Handle<Object> event = httpsys_create_event(uv_httpsys, HTTPSYS_END_REQUEST);
        httpsys_make_callback(event);
    }
    else if (ERROR_IO_PENDING != hr && NO_ERROR != hr)
    {
        // Initiation failed - notify JavaScript
        httpsys_notify_error(uv_httpsys, HTTPSYS_ERROR_INITIALIZING_READ_REQUEST_BODY, hr);
        httpsys_free(uv_httpsys);
        uv_httpsys = NULL;
    }

    // Result of NO_ERROR at this point means synchronous completion that must be handled by the caller
    // since the IO completion port of libuv will not receive a completion.

Error:

    return hr;
}

Handle<Value> httpsys_init(const Arguments& args)
{
    HandleScope handleScope;

    Handle<Object> options = args[0]->ToObject();

    callback.Dispose();
    callback.Clear();
    callback = Persistent<Function>::New(
        Handle<Function>::Cast(options->Get(String::New("callback"))));
    initialBufferSize = options->Get(String::New("initialBufferSize"))->Int32Value();
    requestQueueLength = options->Get(String::New("requestQueueLength"))->Int32Value();
    pendingReadCount = options->Get(String::New("pendingReadCount"))->Int32Value();
    int cacheDuration = options->Get(String::New("cacheDuration"))->Int32Value();
    if (0 > cacheDuration)
    {
        cachePolicy.Policy = HttpCachePolicyNocache;
        cachePolicy.SecondsToLive = 0;
    }
    else 
    {
        cachePolicy.Policy = HttpCachePolicyTimeToLive;
        defaultCacheDuration = cacheDuration;
    }

    return handleScope.Close(Undefined());
}

void httpsys_prepare_new_requests(uv_prepare_t* handle, int status)
{
    uv_httpsys_server_t* uv_httpsys_server = CONTAINING_RECORD(handle, uv_httpsys_server_t, uv_prepare);
    HRESULT hr;
    uv_httpsys_t* uv_httpsys = NULL;

    while (uv_httpsys_server->readsToInitialize)
    {
        // TODO: address a situation when some new requests fail while others not - cancel them?
        ErrorIf(NULL == (uv_httpsys = (uv_httpsys_t*)malloc(sizeof(uv_httpsys_t))),
            ERROR_NOT_ENOUGH_MEMORY);
        RtlZeroMemory(uv_httpsys, sizeof(uv_httpsys_t));
        uv_httpsys->uv_httpsys_server = uv_httpsys_server;
        CheckError(httpsys_initiate_new_request(uv_httpsys));
        uv_httpsys = NULL;
        uv_httpsys_server->readsToInitialize--;
    }

    return;

Error:

    if (NULL != uv_httpsys)
    {
        httpsys_free(uv_httpsys);
        uv_httpsys = NULL;
    }

    httpsys_notify_error(uv_httpsys_server, HTTPSYS_ERROR_INITIALIZING_REQUEST, hr);

    return;
}

Handle<Value> httpsys_listen(const Arguments& args)
{
    HandleScope handleScope;
    HRESULT hr;
    HTTPAPI_VERSION HttpApiVersion = HTTPAPI_VERSION_2;
    WCHAR url[MAX_PATH + 1];
    WCHAR requestQueueName[MAX_PATH + 1];
    HTTP_BINDING_INFO bindingInfo;
    uv_loop_t* loop;
    uv_httpsys_t* uv_httpsys = NULL;
    uv_httpsys_server_t* uv_httpsys_server = NULL;

    // Process arguments

    Handle<Object> options = args[0]->ToObject();

    // Lazy, one-time initialization of HTTP.SYS

    if (!initialized) 
    {
        CheckError(HttpInitialize(
            HttpApiVersion, 
            HTTP_INITIALIZE_SERVER, 
            NULL));
        initialized = 1;
    }

    // Create uv_httpsys_server_t

    ErrorIf(NULL == (uv_httpsys_server = (uv_httpsys_server_t*)malloc(sizeof(uv_httpsys_server_t))),
        ERROR_NOT_ENOUGH_MEMORY);
    RtlZeroMemory(uv_httpsys_server, sizeof(uv_httpsys_server_t));

    // Create the request queue name by replacing slahes in the URL with _
    // to make it a valid file name.

    options->Get(String::New("url"))->ToString()->Write((uint16_t*)requestQueueName, 0, MAX_PATH);

    for (WCHAR* current = requestQueueName; *current; current++)
    {
        if (L'/' == *current)
        {
            *current = L'_';
        }
    }

    // Create HTTP.SYS request queue (one per URL). Request queues are named 
    // to allow sharing between processes and support cluster. 
    // First try to obtain a handle to a pre-existing queue with the name
    // based on the listen URL. If that fails, create a new named request queue. 

    hr = HttpCreateRequestQueue(
        HttpApiVersion,
        requestQueueName,
        NULL,
        HTTP_CREATE_REQUEST_QUEUE_FLAG_OPEN_EXISTING,
        &uv_httpsys_server->requestQueue);

    if (ERROR_FILE_NOT_FOUND == hr)
    {
        // Request queue by that name does not exist yet, try to create it

        CheckError(HttpCreateRequestQueue(
                HttpApiVersion,
                requestQueueName,
                NULL,
                0,
                &uv_httpsys_server->requestQueue));

        // Create HTTP.SYS session and associate it with URL group containing the
        // single listen URL. 

        CheckError(HttpCreateServerSession(
            HttpApiVersion, 
            &uv_httpsys_server->sessionId, 
            NULL));

        CheckError(HttpCreateUrlGroup(
            uv_httpsys_server->sessionId,
            &uv_httpsys_server->groupId,
            NULL));

        options->Get(String::New("url"))->ToString()->Write((uint16_t*)url, 0, MAX_PATH);

        CheckError(HttpAddUrlToUrlGroup(
            uv_httpsys_server->groupId,
            url,
            0,
            NULL));

        // Set the request queue length

        CheckError(HttpSetRequestQueueProperty(
            uv_httpsys_server->requestQueue,
            HttpServerQueueLengthProperty,
            &requestQueueLength,
            sizeof(requestQueueLength),
            0,
            NULL));        

        // Bind the request queue with the URL group to enable receiving
        // HTTP traffic on the request queue. 

        RtlZeroMemory(&bindingInfo, sizeof(HTTP_BINDING_INFO));
        bindingInfo.RequestQueueHandle = uv_httpsys_server->requestQueue;
        bindingInfo.Flags.Present = 1;

        CheckError(HttpSetUrlGroupProperty(
            uv_httpsys_server->groupId,
            HttpServerBindingProperty,
            &bindingInfo,
            sizeof(HTTP_BINDING_INFO)));        
    }
    else
    {
        CheckError(hr);
    }

    // Configure the request queue to prevent queuing a completion to the libuv
    // IO completion port when an async operation completes synchronously. 

    ErrorIf(!SetFileCompletionNotificationModes(
        uv_httpsys_server->requestQueue,
        FILE_SKIP_COMPLETION_PORT_ON_SUCCESS | FILE_SKIP_SET_EVENT_ON_HANDLE),
        GetLastError());

    // Associate the HTTP.SYS request queue handle with the IO completion port 
    // of the default libuv event loop used by node. This will cause 
    // async completions related to the HTTP.SYS request queue to execute 
    // on the node.js thread. The event loop will process these events as
    // UV_ASYNC handle types, beacuse a call to uv_async_init will be made
    // every time an async operation is started with HTTP.SYS. On Windows, 
    // uv_async_init associates the OVERLAPPED structure representing the 
    // async operation with the uv_async_t handle that embeds it, which allows
    // the event loop to map the OVERLAPPED instance back to the async 
    // callback to invoke when the IO completion port is signaled. 

    loop = uv_default_loop();
    ErrorIf(NULL == CreateIoCompletionPort(
        uv_httpsys_server->requestQueue,
        loop->iocp,
        (ULONG_PTR)uv_httpsys_server->requestQueue,
        0), 
        GetLastError());

    // Initiate uv_prepare associated with this server that will be responsible for 
    // initializing new pending receives of new HTTP reqests against HTTP.SYS 
    // to replace completed ones. This logic will run once per iteration of the libuv event loop.
    // The first execution of the callback will initiate the first batch of reads. 

    uv_prepare_init(loop, &uv_httpsys_server->uv_prepare);
    uv_prepare_start(&uv_httpsys_server->uv_prepare, httpsys_prepare_new_requests);
    uv_httpsys_server->readsToInitialize = pendingReadCount;

    // The result wraps the native pointer to the uv_httpsys_server_t structure.
    // It also doubles as an event parameter to JavaScript callbacks scoped to the entire server.

    uv_httpsys_server->event = Persistent<Object>::New(httpsysObject->NewInstance());
    uv_httpsys_server->event->SetPointerInInternalField(0, (void*)uv_httpsys_server);
    uv_httpsys_server->event->Set(v8uv_httpsys_server, uv_httpsys_server->event); 

    return uv_httpsys_server->event;

Error:

    if (NULL != uv_httpsys_server)
    {
        if (HTTP_NULL_ID != uv_httpsys_server->groupId)
        {
            HttpCloseUrlGroup(uv_httpsys_server->groupId);
        }

        if (NULL != uv_httpsys_server->requestQueue)
        {
            HttpCloseRequestQueue(uv_httpsys_server->requestQueue);
        }

        if (HTTP_NULL_ID != uv_httpsys_server->sessionId)
        {
            HttpCloseServerSession(uv_httpsys_server->sessionId);
        }

        free(uv_httpsys_server);
        uv_httpsys_server = NULL;
    }

    if (NULL != uv_httpsys)
    {
        httpsys_free(uv_httpsys);
        uv_httpsys = NULL;
    }

    return handleScope.Close(ThrowException(Int32::New(hr)));
}

Handle<Value> httpsys_stop_listen(const Arguments& args)
{
    HandleScope handleScope;
    HRESULT hr;

    uv_httpsys_server_t* uv_httpsys_server = (uv_httpsys_server_t*)Handle<Object>::Cast(args[0])->GetPointerFromInternalField(0);
    if (HTTP_NULL_ID != uv_httpsys_server->groupId)
    {
        CheckError(HttpCloseUrlGroup(uv_httpsys_server->groupId));
    }

    if (NULL != uv_httpsys_server->requestQueue)
    {
        CheckError(HttpCloseRequestQueue(uv_httpsys_server->requestQueue));
    }

    if (HTTP_NULL_ID != uv_httpsys_server->sessionId)
    {
        CheckError(HttpCloseServerSession(uv_httpsys_server->sessionId));
    }

    uv_prepare_stop(&uv_httpsys_server->uv_prepare);

    // TODO: deallocate uv_httpsys_server and release uv_httpsys_server->event 
    // after graceful close of active requests

    return handleScope.Close(Undefined());

Error:

    return handleScope.Close(ThrowException(Int32::New(hr)));
}

Handle<Value> httpsys_resume(const Arguments& args)
{
    HTTPSYS_EXPORT_PREAMBLE;

    CheckError(httpsys_read_request_body_loop(uv_httpsys));

    return handleScope.Close(Undefined());

Error:

    // uv_httpsys had been freed alredy

    return handleScope.Close(ThrowException(Int32::New(hr)));
}

Handle<Value> httpsys_write_headers(const Arguments& args)
{
    HTTPSYS_EXPORT_PREAMBLE;
    Handle<Object> options = args[0]->ToObject();
    String::Utf8Value reason(options->Get(v8reason)); 
    Handle<Object> unknownHeaders;
    Handle<Array> headerNames;
    Handle<String> headerName;
    Handle<Array> knownHeaders;
    Handle<Object> knownHeader;
    Handle<Value> cacheDuration;
    ULONG flags;

    // Initialize libuv handle representing this async operation

    RtlZeroMemory(&uv_httpsys->uv_async, sizeof(uv_async_t));
    CheckError(uv_async_init(uv_default_loop(), &uv_httpsys->uv_async, httpsys_write_callback));

    // Set response status code and reason

    uv_httpsys->response.StatusCode = options->Get(v8statusCode)->Uint32Value();
    ErrorIf(NULL == (uv_httpsys->response.pReason = (PCSTR)malloc(reason.length())),
        ERROR_NOT_ENOUGH_MEMORY);
    uv_httpsys->response.ReasonLength = reason.length();
    memcpy((void*)uv_httpsys->response.pReason, *reason, reason.length());

    // Set known headers

    knownHeaders = Handle<Array>::Cast(options->Get(v8knownHeaders));
    for (unsigned int i = 0; i < knownHeaders->Length(); i++)
    {
        knownHeader = Handle<Object>::Cast(knownHeaders->Get(i));
        int headerIndex = knownHeader->Get(v8id)->Int32Value();
        String::Utf8Value header(knownHeader->Get(v8value));
        ErrorIf(NULL == (uv_httpsys->response.Headers.KnownHeaders[headerIndex].pRawValue = 
            (PCSTR)malloc(header.length())),
            ERROR_NOT_ENOUGH_MEMORY);
        uv_httpsys->response.Headers.KnownHeaders[headerIndex].RawValueLength = header.length();
        memcpy((void*)uv_httpsys->response.Headers.KnownHeaders[headerIndex].pRawValue, 
            *header, header.length());
    }

    // Set unknown headers

    unknownHeaders = Handle<Object>::Cast(options->Get(v8unknownHeaders));
    headerNames = unknownHeaders->GetOwnPropertyNames();
    if (headerNames->Length() > 0)
    {
        ErrorIf(NULL == (uv_httpsys->response.Headers.pUnknownHeaders = 
            (PHTTP_UNKNOWN_HEADER)malloc(headerNames->Length() * sizeof (HTTP_UNKNOWN_HEADER))),
            ERROR_NOT_ENOUGH_MEMORY);
        RtlZeroMemory(uv_httpsys->response.Headers.pUnknownHeaders, 
            headerNames->Length() * sizeof (HTTP_UNKNOWN_HEADER));
        uv_httpsys->response.Headers.UnknownHeaderCount = headerNames->Length();
        for (int i = 0; i < uv_httpsys->response.Headers.UnknownHeaderCount; i++)
        {
            headerName = headerNames->Get(i)->ToString();
            String::Utf8Value headerNameUtf8(headerName);
            String::Utf8Value headerValueUtf8(unknownHeaders->Get(headerName));
            uv_httpsys->response.Headers.pUnknownHeaders[i].NameLength = headerNameUtf8.length();
            ErrorIf(NULL == (uv_httpsys->response.Headers.pUnknownHeaders[i].pName = 
                (PCSTR)malloc(headerNameUtf8.length())),
                ERROR_NOT_ENOUGH_MEMORY);
            memcpy((void*)uv_httpsys->response.Headers.pUnknownHeaders[i].pName, 
                *headerNameUtf8, headerNameUtf8.length());
            uv_httpsys->response.Headers.pUnknownHeaders[i].RawValueLength = headerValueUtf8.length();
            ErrorIf(NULL == (uv_httpsys->response.Headers.pUnknownHeaders[i].pRawValue = 
                (PCSTR)malloc(headerValueUtf8.length())),
                ERROR_NOT_ENOUGH_MEMORY);
            memcpy((void*)uv_httpsys->response.Headers.pUnknownHeaders[i].pRawValue, 
                *headerValueUtf8, headerValueUtf8.length());
        }
    }

    // Prepare response body and determine flags

    CheckError(httpsys_initialize_body_chunks(options, uv_httpsys, &flags));
    if (uv_httpsys->chunk.FromMemory.pBuffer) 
    {
        uv_httpsys->response.EntityChunkCount = 1;
        uv_httpsys->response.pEntityChunks = &uv_httpsys->chunk;
    }

    // Determine cache policy
    
    if (HttpCachePolicyTimeToLive == cachePolicy.Policy)
    {
        // If HTTP.SYS output caching is enabled, establish the duration to cache for
        // based on the setting on the message or the global default, in that order of precedence

        cacheDuration = options->Get(v8cacheDuration);
        cachePolicy.SecondsToLive = cacheDuration->IsUint32() ? cacheDuration->Uint32Value() : defaultCacheDuration;
    }

    // TOOD: support response trailers

    // Initiate async send of the HTTP response headers and optional body

    hr = HttpSendHttpResponse(
        uv_httpsys->uv_httpsys_server->requestQueue,
        uv_httpsys->requestId,
        flags,
        &uv_httpsys->response,
        &cachePolicy,
        NULL,
        NULL,
        0,
        &uv_httpsys->uv_async.async_req.overlapped,
        NULL);

    if (NO_ERROR == hr)
    {
        // Synchronous completion. 

        httpsys_write_callback(&uv_httpsys->uv_async, 1);
    }
    else 
    {
        ErrorIf(ERROR_IO_PENDING != hr, hr);
    }

    // Return true if async completion is pending and an event will be generated once completed
    return handleScope.Close(Boolean::New(ERROR_IO_PENDING == hr));

Error:

    httpsys_free(uv_httpsys);
    uv_httpsys = NULL;

    return handleScope.Close(ThrowException(Int32::New(hr)));
}

void httpsys_write_callback(uv_async_t* handle, int status)
{
    HTTPSYS_CALLBACK_PREAMBLE;

    // Process async completion

    if (S_OK != (NTSTATUS)uv_httpsys->uv_async.async_req.overlapped.Internal)
    {
        // Async completion failed - notify JavaScript

        httpsys_notify_error(
            uv_httpsys, 
            HTTPSYS_ERROR_WRITING, 
            (unsigned int)uv_httpsys->uv_async.async_req.overlapped.Internal);
        httpsys_free(uv_httpsys);
        uv_httpsys = NULL;
    }
    else 
    {
        // Successful completion 

        if (0 == status)
        {
            // Call completed asynchronously - send notification to JavaScript.

            Handle<Object> event = httpsys_create_event(uv_httpsys, HTTPSYS_WRITTEN);
            httpsys_make_callback(event);
        }

        if (uv_httpsys->lastChunkSent)
        {
            // Response is completed - clean up resources
            httpsys_free(uv_httpsys);
            uv_httpsys = NULL;
        }
    }    
}

HRESULT httpsys_initialize_body_chunks(Handle<Object> options, uv_httpsys_t* uv_httpsys, ULONG* flags)
{
    HRESULT hr;
    HandleScope handleScope;
    Handle<Array> chunks;

    httpsys_free_chunks(uv_httpsys);

    // Copy JavaScript buffers representing response body chunks into a single
    // continuous memory block in an HTTP_DATA_CHUNK. 

    chunks = Handle<Array>::Cast(options->Get(v8chunks));
    if (chunks->Length() > 0)
    {
        for (unsigned int i = 0; i < chunks->Length(); i++) {
            Handle<Object> buffer = chunks->Get(i)->ToObject();
            uv_httpsys->chunk.FromMemory.BufferLength += (ULONG)node::Buffer::Length(buffer);
        }

        ErrorIf(NULL == (uv_httpsys->chunk.FromMemory.pBuffer = 
            malloc(uv_httpsys->chunk.FromMemory.BufferLength)),
            ERROR_NOT_ENOUGH_MEMORY);

        char* position = (char*)uv_httpsys->chunk.FromMemory.pBuffer;
        for (unsigned int i = 0; i < chunks->Length(); i++)
        {
            Handle<Object> buffer = chunks->Get(i)->ToObject();
            memcpy(position, node::Buffer::Data(buffer), node::Buffer::Length(buffer));
            position += node::Buffer::Length(buffer);
        }
    }

    // Remove the 'chunks' propert from the options object to indicate they have been 
    // consumed.

    ErrorIf(!options->Set(v8chunks, Undefined()), E_FAIL);

    // Determine whether the last of the response body is to be written out.

    if (options->Get(v8isLastChunk)->BooleanValue())
    {
        *flags = 0;
        uv_httpsys->lastChunkSent = 1;
    }
    else
    {
        *flags = HTTP_SEND_RESPONSE_FLAG_MORE_DATA;
    }

    return S_OK;

Error:

    httpsys_free_chunks(uv_httpsys);

    return hr;
}

Handle<Value> httpsys_write_body(const Arguments& args)
{
    HTTPSYS_EXPORT_PREAMBLE;
    Handle<Object> options = args[0]->ToObject();
    ULONG flags;

    // Initialize libuv handle representing this async operation

    RtlZeroMemory(&uv_httpsys->uv_async, sizeof(uv_async_t));
    CheckError(uv_async_init(uv_default_loop(), &uv_httpsys->uv_async, httpsys_write_callback));

    // Prepare response body and determine flags

    CheckError(httpsys_initialize_body_chunks(options, uv_httpsys, &flags));

    // Initiate async send of the HTTP response body

    hr = HttpSendResponseEntityBody(
        uv_httpsys->uv_httpsys_server->requestQueue,
        uv_httpsys->requestId,
        flags,
        uv_httpsys->chunk.FromMemory.pBuffer ? 1 : 0,
        uv_httpsys->chunk.FromMemory.pBuffer ? &uv_httpsys->chunk : NULL,
        NULL,
        NULL,
        0,
        &uv_httpsys->uv_async.async_req.overlapped,
        NULL);

    if (NO_ERROR == hr)
    {
        // Synchronous completion. 

        httpsys_write_callback(&uv_httpsys->uv_async, 1);
    }
    else 
    {
        ErrorIf(ERROR_IO_PENDING != hr, hr);
    }

    // Return true if async completion is pending and an event will be generated once completed
    return handleScope.Close(Boolean::New(ERROR_IO_PENDING == hr));

Error:

    httpsys_free(uv_httpsys);
    uv_httpsys = NULL;

    return handleScope.Close(ThrowException(Int32::New(hr)));
}

void init(Handle<Object> target) 
{
    HandleScope handleScope;

    initialized = 0;

    // Create V8 representation of HTTP verb strings to reuse across requests

    for (int i = 0; i < HttpVerbMaximum; i++)
    {
        if (verbs[i])
        {
            v8verbs[i] = Persistent<String>::New(String::New(verbs[i]));
        }
    }

    // Create V8 representation of HTTP header names to reuse across requests

    for (int i = 0; i < HttpHeaderRequestMaximum; i++)
    {
        if (requestHeaders[i])
        {
            v8httpRequestHeaderNames[i] = Persistent<String>::New(String::New(requestHeaders[i]));
        }
    }

    // Create global V8 strings to reuse across requests

    v8method = Persistent<String>::New(String::NewSymbol("method"));
    v8uv_httpsys_server = Persistent<String>::New(String::NewSymbol("uv_httpsys_server"));
    v8req = Persistent<String>::New(String::NewSymbol("req"));
    v8httpHeaders = Persistent<String>::New(String::NewSymbol("headers"));
    v8httpVersionMinor = Persistent<String>::New(String::NewSymbol("httpVersionMinor"));
    v8httpVersionMajor = Persistent<String>::New(String::NewSymbol("httpVersionMajor"));
    v8eventType = Persistent<String>::New(String::NewSymbol("eventType"));
    v8code = Persistent<String>::New(String::NewSymbol("code"));
    v8url = Persistent<String>::New(String::NewSymbol("url"));
    v8uv_httpsys = Persistent<String>::New(String::NewSymbol("uv_httpsys"));
    v8data = Persistent<String>::New(String::NewSymbol("data"));
    v8statusCode = Persistent<String>::New(String::NewSymbol("statusCode"));
    v8reason = Persistent<String>::New(String::NewSymbol("reason"));
    v8knownHeaders = Persistent<String>::New(String::NewSymbol("knownHeaders"));
    v8unknownHeaders = Persistent<String>::New(String::NewSymbol("unknownHeaders"));
    v8isLastChunk = Persistent<String>::New(String::NewSymbol("isLastChunk"));
    v8chunks = Persistent<String>::New(String::NewSymbol("chunks"));
    v8id = Persistent<String>::New(String::NewSymbol("id"));
    v8value = Persistent<String>::New(String::NewSymbol("value"));
    v8cacheDuration = Persistent<String>::New(String::NewSymbol("cacheDuration"));

    // Capture the constructor function of JavaScript Buffer implementation

    bufferConstructor = Persistent<Function>::New(Handle<Function>::Cast(
        Context::GetCurrent()->Global()->Get(String::New("Buffer")))); 

    // Create an object template of an object to roundtrip a native pointer to JavaScript

    httpsysObject = Persistent<ObjectTemplate>::New(ObjectTemplate::New());
    httpsysObject->SetInternalFieldCount(1);

    // Create exports

    NODE_SET_METHOD(target, "httpsys_init", httpsys_init);
    NODE_SET_METHOD(target, "httpsys_listen", httpsys_listen);
    NODE_SET_METHOD(target, "httpsys_stop_listen", httpsys_stop_listen);
    NODE_SET_METHOD(target, "httpsys_resume", httpsys_resume);
    NODE_SET_METHOD(target, "httpsys_write_headers", httpsys_write_headers);
    NODE_SET_METHOD(target, "httpsys_write_body", httpsys_write_body);
}

NODE_MODULE(httpsys, init);
