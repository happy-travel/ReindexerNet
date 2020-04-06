﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReindexerNet;
using ReindexerNet.Embedded;
using ReindexerNet.Embedded.Internal;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Utf8Json;

namespace ReindexerNet.EmbeddedTest
{
    [TestClass]
    public class ReindexerBindingTest
    {
        private UIntPtr _rx;
        private reindexer_ctx_info _ctxInfo = new reindexer_ctx_info { ctx_id = 0, exec_timeout = -1 };
        public TestContext TestContext { get; set; }

        private void AssertError(reindexer_error error)
        {
            Assert.AreEqual(null, error.what);
            Assert.AreEqual(0, error.code);
        }

        void Log(LogLevel level, string msg)
        {
            if (level <= LogLevel.Info)
                TestContext.WriteLine("{0}: {1}", level, msg);
        }

        private LogWriterAction _logWriter;

        [TestInitialize]
        public void InitReindexer()
        {
            _rx = ReindexerBinding.init_reindexer();
            Assert.AreNotEqual(UIntPtr.Zero, _rx);

            _logWriter = new LogWriterAction(Log);
            ReindexerBinding.reindexer_enable_logger(_logWriter);
            Connect();
        }

        [TestCleanup]
        public void DestroyReindexer()
        {
            ReindexerBinding.destroy_reindexer(_rx);
        }

        [TestMethod]
        public void Connect()
        {
            AssertError(
                ReindexerBinding.reindexer_connect(_rx,
                "builtin://TestDb",
                new ConnectOpts
                {
                    options = ConnectOpt.kConnectOptAllowNamespaceErrors,
                    storage = StorageTypeOpt.kStorageTypeOptLevelDB,
                    expectedClusterID = 0
                }, ReindexerBinding.ReindexerVersion));
        }

        [TestMethod]
        public void Ping()
        {
            AssertError(ReindexerBinding.reindexer_ping(_rx));
        }

        [TestMethod]
        public void EnableStorage()
        {
            var dbName = "TestDbForEnableStorage";
            if (Directory.Exists(dbName))
                Directory.Delete(dbName, true);
            AssertError(ReindexerBinding.reindexer_enable_storage(ReindexerBinding.init_reindexer(), dbName, _ctxInfo));
        }

        [TestMethod]
        public void InitSystemNamespaces()
        {
            AssertError(ReindexerBinding.reindexer_init_system_namespaces(_rx));
        }

        [TestMethod]
        public void OpenNamespace()
        {
            AssertError(ReindexerBinding.reindexer_open_namespace(_rx, "OpenNamespaceTest",
                new StorageOpts { options = StorageOpt.kStorageOptCreateIfMissing | StorageOpt.kStorageOptEnabled },
                _ctxInfo));
            AssertError(ReindexerBinding.reindexer_drop_namespace(_rx, "OpenNamespaceTest", _ctxInfo));
        }

        [TestMethod]
        public void DropNamespace()
        {
            var error = ReindexerBinding.reindexer_drop_namespace(_rx, "DropNamespaceTest", _ctxInfo);
            Assert.AreNotEqual(0, error.code);
            AssertError(ReindexerBinding.reindexer_open_namespace(_rx, "DropNamespaceTest",
                new StorageOpts { options = StorageOpt.kStorageOptCreateIfMissing | StorageOpt.kStorageOptEnabled },
                _ctxInfo));
            AssertError(ReindexerBinding.reindexer_drop_namespace(_rx, "DropNamespaceTest", _ctxInfo));
        }

        private const string DataTestNamespace = nameof(DataTestNamespace);

        public void ModifyItemPacked(string itemJson = null)
        {
            AssertError(ReindexerBinding.reindexer_open_namespace(_rx, DataTestNamespace,
                new StorageOpts { options = StorageOpt.kStorageOptCreateIfMissing | StorageOpt.kStorageOptEnabled },
                _ctxInfo));

            var indexDefJson = JsonSerializer.ToJsonString(
            new Index
            {
                Name = "Id",
                IsPk = true,
                FieldType = FieldType.Int64,
                IndexType = IndexType.Hash,
                JsonPaths = new List<string> { "Id" }
            }, Utf8Json.Resolvers.StandardResolver.ExcludeNull);
            AssertError(ReindexerBinding.reindexer_add_index(_rx, DataTestNamespace, indexDefJson, _ctxInfo));
            indexDefJson = JsonSerializer.ToJsonString(
            new Index
            {
                Name = "Guid",
                IsPk = false,
                FieldType = FieldType.String,
                IndexType = IndexType.Hash,
                JsonPaths = new List<string> { "Guid" }
            }, Utf8Json.Resolvers.StandardResolver.ExcludeNull);
            AssertError(ReindexerBinding.reindexer_add_index(_rx, DataTestNamespace, indexDefJson, _ctxInfo));

            var rsp = ReindexerBinding.reindexer_select(_rx,
                $"SELECT \"indexes.name\" FROM #namespaces WHERE name = \"{DataTestNamespace}\"",
                1, new int[0], 0, _ctxInfo);

            if (rsp.err_code != 0)
                Assert.AreEqual(null, (string)rsp.@out);
            Assert.AreNotEqual(UIntPtr.Zero, rsp.@out.results_ptr);

            var (json, offsets, explain) = BindingHelpers.RawResultToJson(rsp.@out, "items", "total_count");
            var indexNames = JsonSerializer.Deserialize<ItemsOf<Namespace>>(json).Items.SelectMany(n => n.Indexes.Select(i => i.Name)).ToList();

            CollectionAssert.Contains(indexNames as ICollection, "Id");
            CollectionAssert.Contains(indexNames as ICollection, "Guid");

            using (var ser1 = new CJsonWriter())
            {
                ser1.PutVString(DataTestNamespace);
                ser1.PutVarCUInt((int)DataFormat.FormatJson);//format);
                ser1.PutVarCUInt((int)ItemModifyMode.ModeUpsert);//mode);
                ser1.PutVarCUInt(0);//stateToken);
                ser1.PutVarCUInt(0);//len(precepts));

                reindexer_buffer.PinBufferFor(ser1.CurrentBuffer, args =>
                {
                    using (var data = reindexer_buffer.From(Encoding.UTF8.GetBytes(itemJson ?? $"{{\"Id\":1, \"Guid\":\"{Guid.NewGuid()}\"}}")))
                    {
                        var rsp = ReindexerBinding.reindexer_modify_item_packed(_rx, args, data.Buffer, _ctxInfo);
                        if (rsp.err_code != 0)
                            Assert.AreEqual(null, (string)rsp.@out);

                        Assert.AreNotEqual(UIntPtr.Zero, rsp.@out.results_ptr);

                        var reader = new CJsonReader(rsp.@out);
                        var rawQueryParams = reader.ReadRawQueryParams();

                        Assert.AreEqual(1, rawQueryParams.count);

                        var resultp = reader.ReadRawItemParams();

                        if (rsp.@out.results_ptr != UIntPtr.Zero)
                            AssertError(ReindexerBinding.reindexer_free_buffer(rsp.@out));
                    }
                });
            }
        }

        [TestMethod]
        public void SelectSql()
        {
            ModifyItemPacked();

            var rsp = ReindexerBinding.reindexer_select(_rx,
                $"SELECT * FROM {DataTestNamespace}",
                1, new int[0], 0, _ctxInfo);
            if (rsp.err_code != 0)
                Assert.AreEqual(null, (string)rsp.@out);
            Assert.AreNotEqual(UIntPtr.Zero, rsp.@out.results_ptr);

            var (json, offsets, explain) = BindingHelpers.RawResultToJson(rsp.@out, "items", "total_count");

            Assert.AreNotEqual(0, json.Length);
            Assert.AreNotEqual(0, offsets.Count);
        }

        [TestMethod]
        public void ExplainSelectSql()
        {
            ModifyItemPacked();

            var rsp = ReindexerBinding.reindexer_select(_rx,
                $"EXPLAIN SELECT * FROM {DataTestNamespace}",
                1, new int[0], 0, _ctxInfo);
            if (rsp.err_code != 0)
                Assert.AreEqual(null, (string)rsp.@out);
            Assert.AreNotEqual(UIntPtr.Zero, rsp.@out.results_ptr);

            var (json, offsets, explain) = BindingHelpers.RawResultToJson(rsp.@out, "items", "total_count");

            Assert.AreNotEqual(0, json.Length);
            Assert.AreNotEqual(0, offsets.Count);
            Assert.AreNotEqual(0, explain.Length);

            var explainDef = JsonSerializer.Deserialize<ExplainDef>(explain);
            Assert.IsNotNull(explainDef);
        }

        [TestMethod]
        public void DeleteSql()
        {
            AssertError(ReindexerBinding.reindexer_open_namespace(_rx, DataTestNamespace,
               new StorageOpts { options = StorageOpt.kStorageOptCreateIfMissing | StorageOpt.kStorageOptEnabled },
               _ctxInfo));

            ModifyItemPacked($"{{\"Id\":2, \"Guid\":\"{Guid.NewGuid()}\"}}");

            var delRsp = ReindexerBinding.reindexer_select(_rx,
                $"DELETE FROM {DataTestNamespace} WHERE Id=2",
                1, new int[0], 0, _ctxInfo);
            if (delRsp.err_code != 0)
                Assert.AreEqual(null, (string)delRsp.@out);
            Assert.AreNotEqual(UIntPtr.Zero, delRsp.@out.results_ptr);

            var (json, offsets, explain) = BindingHelpers.RawResultToJson(delRsp.@out, "items", "total_count");
            Assert.AreNotEqual(0, json.Length);
            Assert.AreNotEqual(0, offsets.Count);

            var selRsp = ReindexerBinding.reindexer_select(_rx,
                $"SELECT * FROM {DataTestNamespace} WHERE Id=2",
                1, new int[] { 0 }, 1, _ctxInfo);
            if (selRsp.err_code != 0)
                Assert.AreEqual(null, (string)selRsp.@out);
            Assert.AreNotEqual(UIntPtr.Zero, selRsp.@out.results_ptr);

            (json, offsets, explain) = BindingHelpers.RawResultToJson(selRsp.@out, "items", "total_count");
            Assert.AreNotEqual(0, json.Length);
            Assert.AreEqual(0, offsets.Count);
        }


        [TestMethod]
        public void ParallelModifyItemPacked()
        {
            var nsName = "ParallelTestNs";
            AssertError(ReindexerBinding.reindexer_open_namespace(_rx, nsName,
                new StorageOpts { options = StorageOpt.kStorageOptCreateIfMissing | StorageOpt.kStorageOptEnabled },
                _ctxInfo));

            var indexDefJson = JsonSerializer.ToJsonString(
            new Index
            {
                Name = "Id",
                IsPk = true,
                FieldType = FieldType.Int64,
                IndexType = IndexType.Hash,
                JsonPaths = new List<string> { "Id" }
            }, Utf8Json.Resolvers.StandardResolver.ExcludeNull);
            AssertError(ReindexerBinding.reindexer_add_index(_rx, nsName, indexDefJson, _ctxInfo));

            using (var ser1 = new CJsonWriter())
            {
                ser1.PutVString(nsName);
                ser1.PutVarCUInt((int)DataFormat.FormatJson);
                ser1.PutVarCUInt((int)ItemModifyMode.ModeUpsert);
                ser1.PutVarCUInt(0);
                ser1.PutVarCUInt(0);
                
                reindexer_buffer.PinBufferFor(ser1.CurrentBuffer, args =>
                {
                    Parallel.For(0, 300000, i =>
                    {
                        var dataHandle = reindexer_buffer.From(Encoding.UTF8.GetBytes($"{{\"Id\":{i}, \"Guid\":\"{Guid.NewGuid()}\"}}"));
                        var rsp = ReindexerBinding.reindexer_modify_item_packed(_rx, args, dataHandle.Buffer, _ctxInfo);
                        try
                        {
                            if (rsp.err_code != 0)
                                Assert.AreEqual(null, (string)rsp.@out);

                            Assert.AreNotEqual(UIntPtr.Zero, rsp.@out.results_ptr);

                            var reader = new CJsonReader(rsp.@out);
                            var rawQueryParams = reader.ReadRawQueryParams();

                            Assert.AreEqual(1, rawQueryParams.count);

                            var resultp = reader.ReadRawItemParams();
                        }
                        finally
                        {
                            dataHandle.Dispose();
                            rsp.@out.Free();
                        }
                    });
                });
            }

            Thread.Sleep(6000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            AssertError(ReindexerBinding.reindexer_truncate_namespace(_rx, nsName, _ctxInfo));
        }
    }
}
