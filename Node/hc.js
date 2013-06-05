engine = require(__dirname + '/node_modules/httpsys/lib/httpsys.js').http();

var cluster = require('cluster');
var numCPUs = require('os').cpus().length;

if (cluster.isMaster) {
    console.log("CPUs:" + numCPUs);
    for (var i = 0; i < numCPUs; i++) {
        cluster.fork(process.env);
    }

    cluster.on('exit', function(worker, code, signal) {
        console.log('worker ' + worker.process.pid + ' died');
    });
}
else {
    engine.createServer(function (req, res) {
        res.writeHead(200, { 'Content-Type': 'text/plain' });
        res.end('Hello, world!');
    }).listen(8888);
}
