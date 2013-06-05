engine = require(__dirname + '/node_modules/httpsys/lib/httpsys.js').http();

engine.createServer(function (req, res) {
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('Hello world!');
}).listen(8888);
