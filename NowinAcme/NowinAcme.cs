using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Oocx.ACME.Common;

namespace NowinAcme
{
    public static class NowinAcme
    {
        class LoggerProxy: ILog
        {
            readonly IAcmeConfiguration _cfg;

            public LoggerProxy(IAcmeConfiguration cfg)
            {
                _cfg = cfg;
            }

            public void Verbose(string message, params object[] args)
            {
                _cfg.LogVerbose(message,args);
            }

            public void Info(string message, params object[] args)
            {
                _cfg.LogInfo(message, args);
            }

            public void Warning(string message, params object[] args)
            {
                _cfg.LogWarning(message, args);
            }

            public void Error(string message, params object[] args)
            {
                _cfg.LogError(message, args);
            }
        }

        public static Task RedirectToHttps(IDictionary<string, object> env)
        {
            var query = (string)env["owin.RequestQueryString"];
            var loc = "https://" + ((IDictionary<string, string[]>)env["owin.RequestHeaders"])["Host"].First() +
                      env["owin.RequestPath"] + (query.Length > 0 ? "?" + query : "");
            env["owin.ResponseStatusCode"] = 301;
            ((IDictionary<string, string[]>)env["owin.ResponseHeaders"]).Add("Location", new[] { loc });
            return Task.CompletedTask;
        }

        // This must run on server port 80 - Let's encrypt does not allow anything else
        public static Func<IDictionary<string, object>, Task> Use(Func<IDictionary<string, object>, Task> next, IAcmeConfiguration cfg)
        {
            string challengePath = null;
            byte[] challengeContent = null;
            Log.Level = LogLevel.Verbose;
            Log.Current = new LoggerProxy(cfg);
            Func<Task, Task> updateWorker = null;
            updateWorker=async task =>
                {
                    var utcNow = DateTime.UtcNow;
                    var lastUpdate = cfg.LastUpdate;
                    if (utcNow - lastUpdate > TimeSpan.FromDays(30))
                    {
                        await new AcmeProcess(cfg.Email, cfg.Domain, cfg.UpdateCertificate, (path, content) =>
                        {
                            Log.Info($"SET {path} = {content}");

                            challengePath = path;
                            challengeContent = content;
                        }).StartAsync();
                    }
#pragma warning disable 4014
                    // This cannot be awaited because it has to run once in a while without holding any resources.
                    // ReSharper disable once AssignNullToNotNullAttribute
                    Task.Delay(TimeSpan.FromMinutes(15)).ContinueWith(updateWorker);
#pragma warning restore 4014
                };
            Task.Delay(1).ContinueWith(updateWorker);
            return env =>
            {
                var path = (string)env["owin.RequestPath"];
                if (path == challengePath)
                {
                    var respBody = (Stream)env["owin.ResponseBody"];
                    respBody.Write(challengeContent, 0, challengeContent.Length);
                    return Task.CompletedTask;
                }
                return next(env);
            };
        }
    }
}