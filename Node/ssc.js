var cluster = require('cluster');
var https = require('https');
var fs = require('fs');
var numCPUs = require('os').cpus().length;

if (cluster.isMaster) {
  // Fork workers.
  console.log("CPUs:"+numCPUs);
  for (var i = 0; i < numCPUs; i++) {
    cluster.fork();
  }

  cluster.on('exit', function(worker, code, signal) {
    console.log('worker ' + worker.process.pid + ' died');
  });
} else {
  // Workers can share any TCP connection
  // In this case its a HTTP server

  var options = {
    pfx: fs.readFileSync('../sslcert/test.pfx'),
    passphrase: "nowin"
  };
  https.createServer(options,function(req, res) {
    res.writeHead(200, {'Content-Type': 'text/plain'});
    res.end("Hello World!");
  }).listen(8888);
}
