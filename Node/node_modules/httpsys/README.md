httpsys - native HTTP stack for node.js on Windows
===

The `httpsys` module is a native HTTP stack for node.js applications on Windows. It is based on HTTP.SYS. 
Compared to the built in HTTP stack in node.js it offers kernel mode output caching, port sharing, and kernel mode SSL configuration. Once WebSocket support is added, it will only work starting from Windows 8. Cluster is supported. The module aspires to provide high level of API and behavior compatibility with the built in HTTP stack in node.js. 

This is a very early version of the module. Not much testing or performance optimization had been done. The module had been developed against node.js 0.8.7 x86 (but should work against 0.8.x x86). The x64 version is not supported yet. Any and all feedback is welcome [here](https://github.com/tjanczuk/httpsys/issues/new).

See early [performance comparison with the built-in HTTP stack](https://github.com/tjanczuk/httpsys/wiki).

More documentation will come; here is how to get started:

```
npm install httpsys
```

Then in your code:

```javascript
var http = require('httpsys').http();

http.createServer(function (req, res) {
  res.writeHead(200, { 'Content-Type': 'text/plain' });
  res.end('Hello, world!');
}).listen(8080);
```

To use port sharing, provide a full [URL prefix string](http://msdn.microsoft.com/en-us/library/windows/desktop/aa364698(v=vs.85\).aspx) in the call to `Server.listen`, e.g.:

```javascript
var http = require('httpsys').http();

http.createServer(function (req, res) {
  // ...
}).listen('http://*:8080/foo/');
```

At the same time, you can start another process that listens on a different URL prefix on the same port, e.g. `http://*:8080/bar/`. Each of the processes will only receive requests matching the URL prefix they registered for. 

To inspect or modify HTTP.SYS configuration underlying your server use the `netsh http` command in Windows. This allows you to set various timeout values as well as configure SSL certificates. 

Any and all feedback is welcome [here](https://github.com/tjanczuk/httpsys/issues/new).
