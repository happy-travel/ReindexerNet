﻿using ReindexerNet.Embedded.Helpers;
using ReindexerNet.Embedded.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReindexerNet.Embedded
{
    public class ReindexerEmbeddedServer : ReindexerEmbedded
    {
        static ReindexerEmbeddedServer()
        {
            ReindexerBinding.init_reindexer_server();
        }

        internal ReindexerEmbeddedServer()
        {
            //Rx'i connect anında oluşturacaz.            
        }

        private const string _defaultServerYamlConfig = @"
  storage:
    path: {0}
    engine: leveldb
    startwitherrors: false
    autorepair: false
  net:
    httpaddr: {1}
    rpcaddr: {2}
    #webroot:
    security: false
  logger:
    serverlog: stdout
    corelog: stdout
    httplog: stdout
    #rpclog: stdout
    loglevel: trace
  system:
    user:
  debug:
    pprof: false
    allocs: false
  metrics:
    prometheus: false
    collect_period: 1000
    clientsstats: false
";

        public override void Connect(string connectionString, ConnectionOptions options = null)
        {
            var config = new Dictionary<string, string>
            {
                ["httpaddr"] = "0.0.0.0:9088",
                ["rpcaddr"] = "0.0.0.0:6534",
                ["dbname"] = null,
                ["storagepath"] = Path.Combine(Path.GetTempPath(), "ReindexerEmbeddedServer"),
                ["user"] = null,
                ["pass"] = null
            };

            var connStringParts = connectionString.Split(';');
            foreach (var (key, value) in connStringParts.Select(p => p.Split('=')).Select(p => p.Length > 1 ? (p[0].ToLowerInvariant(), p[1]) : (p[0].ToLowerInvariant(), "")))
            {
                config[key] = value;
            }

            if (config["dbname"] == null)
            {
                throw new ArgumentException("You must provide a db name with 'dbname' config key.");
            }

            var dbPath = Path.Combine(config["storagepath"], config["dbname"]);
            if (!Directory.Exists(dbPath))
            {
                Directory.CreateDirectory(dbPath); //reindexer sometimes throws permission exception from c++ mkdir func. so we try to crate directory before.
            }

            Stop();
            Rx = default;
            Start(string.Format(_defaultServerYamlConfig, config["storagepath"], config["httpaddr"], config["rpcaddr"]));            
            Assert.ThrowIfError(() => ReindexerBinding.get_reindexer_instance(config["dbname"], config["user"], config["pass"], ref Rx));
        }

        private long _isServerThreadStarted = 0;
        private readonly object _serverStartupLocker = new object();

        public void Start(string serverConfigYaml)
        {
            lock (_serverStartupLocker) //for not spinning extra threads and double checking lock.
            {
                if (Interlocked.Read(ref _isServerThreadStarted) == 1) //Interlocked for not extra locking in future to check is startup
                {
                    return;
                }

                new Thread(() =>
                {
                    try
                    {
                        ReindexerBinding.start_reindexer_server(serverConfigYaml);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.Message);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isServerThreadStarted, 0);
                    }
                })
                {
                    Name = nameof(ReindexerEmbeddedServer),
                    IsBackground = true
                }.Start();
                Interlocked.Exchange(ref _isServerThreadStarted, 1);
            }

            var waitTimeout = TimeSpan.FromSeconds(5);
            var startTime = DateTime.UtcNow;
            while (ReindexerBinding.check_server_ready() == 0)
            {
                if (DateTime.UtcNow - startTime > waitTimeout)
                    throw new TimeoutException($"Reindexer Embedded Server couldn't be started in {waitTimeout.TotalSeconds} seconds. Check configs.");
                Thread.Sleep(100);
            }
        }

        public void Stop()
        {
            Assert.ThrowIfError(() =>
               ReindexerBinding.stop_reindexer_server()
            );
        }
    }
}
