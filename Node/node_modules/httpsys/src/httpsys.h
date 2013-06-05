#ifndef __HTTPSYS_H
#define __HTTPSYS_H

// TODO: implement httpsys_resume

#include <SDKDDKVer.h>
#include <node.h>
#include <node_buffer.h>
#include <v8.h>
#include <uv.h>
#include <http.h>

using namespace v8;

#pragma comment(lib, "httpapi.lib")

#define ErrorIf(expr, hresult)    \
    if (expr)                     \
    {                             \
        hr = hresult;             \
        goto Error;               \
    }

#define CheckError(hresult)       \
    {                             \
        HRESULT tmp_hr = hresult; \
        if (S_OK != tmp_hr)       \
        {                         \
            hr = tmp_hr;          \
            goto Error;           \
        }                         \
    }

// Wrapper of the uv_prepare_t associated with an active server

typedef struct uv_httpsys_server_s {
    uv_prepare_t uv_prepare;
    HTTP_SERVER_SESSION_ID sessionId;
    HTTP_URL_GROUP_ID groupId;
    HANDLE requestQueue;
    unsigned int readsToInitialize;
    Persistent<Object> event;
} uv_httpsys_server_t;

// Wrapper of the uv_async_t with HTTP.SYS specific data

typedef struct uv_httpsys_s {
    uv_async_t uv_async;
    HTTP_REQUEST_ID requestId;
    HTTP_RESPONSE response;
    void* buffer;
    unsigned int bufferSize;
    HTTP_DATA_CHUNK chunk;
    int lastChunkSent;
    uv_httpsys_server_t* uv_httpsys_server;
    Persistent<Object> event;
} uv_httpsys_t;

// Types of events passed to the JavaScript callback from native

typedef enum {
    HTTPSYS_ERROR_INITIALIZING_REQUEST = 1,
    HTTPSYS_ERROR_NEW_REQUEST,
    HTTPSYS_NEW_REQUEST,
    HTTPSYS_ERROR_INITIALIZING_READ_REQUEST_BODY,
    HTTPSYS_END_REQUEST,
    HTTPSYS_ERROR_READ_REQUEST_BODY,
    HTTPSYS_REQUEST_BODY,
    HTTPSYS_WRITTEN,
    HTTPSYS_ERROR_WRITING
} uv_httpsys_event_type;

// Utility functions

Handle<Object> httpsys_create_event(uv_httpsys_t* uv_httpsys, int eventType);
Handle<Object> httpsys_create_event(uv_httpsys_server_t* uv_httpsys_server, int eventType);
Handle<Value> httpsys_notify_error(uv_httpsys_t* uv_httpsys, uv_httpsys_event_type errorType, unsigned int code);
Handle<Value> httpsys_notify_error(uv_httpsys_server_t* uv_httpsys_server, uv_httpsys_event_type errorType, unsigned int code);
void httpsys_free_chunks(uv_httpsys_t* uv_httpsys);
void httpsys_free(uv_httpsys_t* uv_httpsys);
Handle<Value> httpsys_make_callback(Handle<Value> options);
HRESULT httpsys_initialize_body_chunks(Handle<Object> options, uv_httpsys_t* uv_httpsys, ULONG* flags);

// HTTP processing state machine actions and events

void httpsys_new_request_callback(uv_async_t* handle, int status);
void httpsys_prepare_new_requests(uv_prepare_t* handle, int status);
HRESULT httpsys_initiate_new_request(uv_httpsys_t* uv_httpsys);
void httpsys_read_request_body_callback(uv_async_t* handle, int status);
HRESULT httpsys_read_request_body_loop(uv_httpsys_t* uv_httpsys);
HRESULT httpsys_initiate_read_request_body(uv_httpsys_t* uv_httpsys);
void httpsys_write_callback(uv_async_t* handle, int status);

// Exports

Handle<Value> httpsys_init(const Arguments& args);
Handle<Value> httpsys_listen(const Arguments& args);
Handle<Value> httpsys_stop_listen(const Arguments& args);
Handle<Value> httpsys_resume(const Arguments& args);
Handle<Value> httpsys_write_headers(const Arguments& args);
Handle<Value> httpsys_write_body(const Arguments& args);

void init(Handle<Object> target);

#endif
