using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Libplanet.Action;
using Libplanet.Blocks;
using Libplanet.Serialization;
using Libplanet.Tx;
using LiteDB;
using Serilog;
using Zio;
using Zio.FileSystems;
using FileMode = LiteDB.FileMode;

namespace Libplanet.Store
{
    /// <summary>
    /// The default built-in <see cref="IStore"/> implementation.  This stores data in
    /// the file system or in memory.  It also uses <a href="https://www.litedb.org/">LiteDB</a>
    /// for some complex indices.
    /// </summary>
    /// <seealso cref="IStore"/>
    public class DefaultStore : BaseStore, IDisposable
    {
        private const string IndexColPrefix = "index_";

        private const string StateRefIdPrefix = "stateref_";

        private const string TxNonceIdPrefix = "nonce_";

        private static readonly UPath TxRootPath = UPath.Root / "tx";

        private readonly ILogger _logger;

        private readonly IFileSystem _root;
        private readonly SubFileSystem _txs;

        private readonly MemoryStream _memoryStream;

        private readonly LiteDatabase _db;

        private readonly ReaderWriterLockSlim _blockLock;

        /// <summary>
        /// Creates a new <seealso cref="DefaultStore"/>.
        /// </summary>
        /// <param name="path">The path of the directory where the storage files will be saved.
        /// If the path is <c>null</c>, the database is created in memory.</param>
        /// <param name="journal">
        /// Enables or disables double write check to ensure durability.
        /// </param>
        /// <param name="cacheSize">Max number of pages in the cache.</param>
        /// <param name="flush">Writes data direct to disk avoiding OS cache.  Turned on by default.
        /// </param>
        /// <param name="readOnly">Opens database readonly mode. Turned off by default.</param>
        public DefaultStore(
            string path,
            bool journal = true,
            int cacheSize = 50000,
            bool flush = true,
            bool readOnly = false
        )
        {
            _logger = Log.ForContext<DefaultStore>();

            if (path is null)
            {
                _root = new MemoryFileSystem();
                _memoryStream = new MemoryStream();
                _db = new LiteDatabase(_memoryStream);
            }
            else
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                var pfs = new PhysicalFileSystem();
                _root = new SubFileSystem(
                    pfs,
                    pfs.ConvertPathFromInternal(path),
                    owned: true
                );

                var connectionString = new ConnectionString
                {
                    Filename = Path.Combine(path, "index.ldb"),
                    Journal = journal,
                    CacheSize = cacheSize,
                    Flush = flush,
                };

                if (readOnly)
                {
                    connectionString.Mode = FileMode.ReadOnly;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                    Type.GetType("Mono.Runtime") is null)
                {
                    // macOS + .NETCore doesn't support shared lock.
                    connectionString.Mode = FileMode.Exclusive;
                }

                _db = new LiteDatabase(connectionString);
            }

            lock (_db.Mapper)
            {
                _db.Mapper.RegisterType(
                    hash => hash.ToByteArray(),
                    b => new HashDigest<SHA256>(b));
                _db.Mapper.RegisterType(
                    txid => txid.ToByteArray(),
                    b => new TxId(b));
                _db.Mapper.RegisterType(
                    address => address.ToByteArray(),
                    b => new Address(b.AsBinary));
            }

            _root.CreateDirectory(TxRootPath);
            _txs = new SubFileSystem(_root, TxRootPath, owned: false);

            _blockLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        private LiteCollection<StagedTxIdDoc> StagedTxIds =>
            _db.GetCollection<StagedTxIdDoc>("staged_txids");

        /// <inheritdoc/>
        public override IEnumerable<Guid> ListChainIds()
        {
            return _db.GetCollectionNames()
                .Where(name => name.StartsWith(IndexColPrefix))
                .Select(name => ParseChainId(name.Substring(IndexColPrefix.Length)));
        }

        /// <inheritdoc/>
        public override void DeleteChainId(Guid chainId)
        {
            _db.DropCollection(IndexCollection(chainId).Name);
            _db.DropCollection(TxNonceId(chainId));
            _db.DropCollection(StateRefId(chainId));
        }

        /// <inheritdoc />
        public override Guid? GetCanonicalChainId()
        {
            LiteCollection<BsonDocument> collection = _db.GetCollection<BsonDocument>("canon");
            var docId = new BsonValue("canon");
            BsonDocument doc = collection.FindById(docId);
            if (doc is null)
            {
                return null;
            }

            return doc.TryGetValue("chainId", out BsonValue ns)
                ? new Guid(ns.AsBinary)
                : (Guid?)null;
        }

        /// <inheritdoc />
        public override void SetCanonicalChainId(Guid chainId)
        {
            LiteCollection<BsonDocument> collection = _db.GetCollection<BsonDocument>("canon");
            var docId = new BsonValue("canon");
            byte[] idBytes = chainId.ToByteArray();
            collection.Upsert(docId, new BsonDocument() { ["chainId"] = new BsonValue(idBytes) });
        }

        /// <inheritdoc/>
        public override long CountIndex(Guid chainId)
        {
            return IndexCollection(chainId).Count();
        }

        /// <inheritdoc/>
        public override IEnumerable<HashDigest<SHA256>> IterateIndexes(
            Guid chainId,
            int offset,
            int? limit)
        {
            return IndexCollection(chainId)
                .Find(Query.All(), offset, limit ?? int.MaxValue)
                .Select(i => i.Hash);
        }

        /// <inheritdoc/>
        public override HashDigest<SHA256>? IndexBlockHash(Guid chainId, long index)
        {
            if (index < 0)
            {
                index += CountIndex(chainId);

                if (index < 0)
                {
                    return null;
                }
            }

            return IndexCollection(chainId).FindById(index + 1)?.Hash;
        }

        /// <inheritdoc/>
        public override long AppendIndex(Guid chainId, HashDigest<SHA256> hash)
        {
            return IndexCollection(chainId).Insert(new HashDoc { Hash = hash }) - 1;
        }

        /// <inheritdoc/>
        public override bool DeleteIndex(Guid chainId, HashDigest<SHA256> hash)
        {
            int deleted = IndexCollection(chainId).Delete(i => i.Hash.Equals(hash));
            return deleted > 0;
        }

        /// <inheritdoc/>
        public override void ForkBlockIndexes(
            Guid sourceChainId,
            Guid destinationChainId,
            HashDigest<SHA256> branchPoint)
        {
            LiteCollection<HashDoc> srcColl = IndexCollection(sourceChainId);
            LiteCollection<HashDoc> destColl = IndexCollection(destinationChainId);

            destColl.InsertBulk(srcColl.FindAll().TakeWhile(i => !i.Hash.Equals(branchPoint)));
            AppendIndex(destinationChainId, branchPoint);
        }

        /// <inheritdoc/>
        public override IEnumerable<Address> ListAddresses(Guid chainId)
        {
            string collId = StateRefId(chainId);
            return _db.GetCollection<StateRefDoc>(collId)
                .Find(Query.All("Address", Query.Ascending))
                .Select(doc => doc.Address)
                .ToImmutableHashSet();
        }

        /// <inheritdoc/>
        public override void StageTransactionIds(IImmutableSet<TxId> txids)
        {
            StagedTxIds.InsertBulk(
                txids.Select(txid => new StagedTxIdDoc { TxId = txid, })
            );
        }

        /// <inheritdoc/>
        public override void UnstageTransactionIds(ISet<TxId> txids)
        {
            StagedTxIds.Delete(tx => txids.Contains(tx.TxId));
        }

        /// <inheritdoc/>
        public override IEnumerable<TxId> IterateStagedTransactionIds()
        {
            IEnumerable<StagedTxIdDoc> docs = StagedTxIds.FindAll();
            return docs.Select(d => d.TxId).Distinct();
        }

        /// <inheritdoc/>
        public override IEnumerable<TxId> IterateTransactionIds()
        {
            foreach (UPath path in _txs.EnumerateDirectories("/"))
            {
                string upper = path.GetName();
                if (upper.Length != 2)
                {
                    continue;
                }

                foreach (UPath subPath in _txs.EnumerateFiles(path))
                {
                    string lower = subPath.GetName();
                    if (lower.Length != 62)
                    {
                        continue;
                    }

                    string name = upper + lower;
                    TxId txid;
                    try
                    {
                        txid = new TxId(ByteUtil.ParseHex(name));
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    yield return txid;
                }
            }
        }

        /// <inheritdoc/>
        public override Transaction<T> GetTransaction<T>(TxId txid)
        {
            UPath path = TxPath(txid);
            if (!_txs.FileExists(path))
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = _txs.ReadAllBytes(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            return Transaction<T>.FromBencodex(bytes);
        }

        /// <inheritdoc/>
        public override void PutTransaction<T>(Transaction<T> tx)
        {
            byte[] txBytes = tx.ToBencodex(true);
            UPath path = TxPath(tx.Id);
            UPath dirPath = path.GetDirectory();
            CreateDirectoryRecursively(_txs, dirPath);

            if (_root is MemoryFileSystem)
            {
                _txs.WriteAllBytes(path, txBytes);
            }
            else
            {
                // For atomicity, writes bytes into an intermediate temp file,
                // and then renames it to the final destination.
                UPath tmpPath = dirPath / $".{Guid.NewGuid():N}.tmp";
                try
                {
                    _txs.WriteAllBytes(tmpPath, txBytes);
                    try
                    {
                        _txs.MoveFile(tmpPath, path);
                    }
                    catch (IOException)
                    {
                        if (_txs.FileExists(path) && _txs.GetFileLength(path) == txBytes.LongLength)
                        {
                            // For the same txid, the content is the same as well.  Don't have
                            // to be written more than once.
                            return;
                        }

                        throw;
                    }
                }
                finally
                {
                    if (_txs.FileExists(tmpPath))
                    {
                        _txs.DeleteFile(tmpPath);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override bool DeleteTransaction(TxId txid)
        {
            var path = TxPath(txid);
            if (_txs.FileExists(path))
            {
                _txs.DeleteFile(path);
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public override IEnumerable<HashDigest<SHA256>> IterateBlockHashes()
        {
            _blockLock.EnterReadLock();
            try
            {
                return _db.FileStorage
                    .Find("block/")
                    .Select(file => new HashDigest<SHA256>(ByteUtil.ParseHex(file.Filename)));
            }
            finally
            {
                _blockLock.ExitReadLock();
            }
        }

        /// <inheritdoc/>
        public override void PutBlock<T>(Block<T> block)
        {
            // checks whether a block already exists or not.
            if (!(_db.FileStorage.FindById(BlockFileId(block.Hash)) is null))
            {
                return;
            }

            _blockLock.EnterWriteLock();
            try
            {
                foreach (Transaction<T> tx in block.Transactions)
                {
                    PutTransaction(tx);
                }

                UploadFile(
                    BlockFileId(block.Hash),
                    ByteUtil.Hex(block.Hash.ToByteArray()),
                    block.ToBencodex(true, false)
                );
            }
            finally
            {
                _blockLock.ExitWriteLock();
            }
        }

        /// <inheritdoc/>
        public override bool DeleteBlock(HashDigest<SHA256> blockHash)
        {
            _blockLock.EnterWriteLock();
            try
            {
                return _db.FileStorage.Delete(BlockFileId(blockHash));
            }
            finally
            {
                _blockLock.ExitWriteLock();
            }
        }

        /// <inheritdoc/>
        public override AddressStateMap GetBlockStates(HashDigest<SHA256> blockHash)
        {
            LiteFileInfo file =
                _db.FileStorage.FindById(BlockStateFileId(blockHash));
            if (file is null)
            {
                return null;
            }

            using (var stream = new MemoryStream())
            {
                DownloadFile(file, stream);
                stream.Seek(0, SeekOrigin.Begin);
                var formatter = new BencodexFormatter<AddressStateMap>();
                return (AddressStateMap)formatter.Deserialize(stream);
            }
        }

        /// <inheritdoc/>
        public override void SetBlockStates(
            HashDigest<SHA256> blockHash,
            AddressStateMap states)
        {
            using (var stream = new MemoryStream())
            {
                var formatter = new BencodexFormatter<AddressStateMap>();
                formatter.Serialize(stream, states);
                stream.Seek(0, SeekOrigin.Begin);
                _db.FileStorage.Upload(
                    BlockStateFileId(blockHash),
                    ByteUtil.Hex(blockHash.ToByteArray()),
                    stream
                );
            }
        }

        /// <inheritdoc/>
        public override IEnumerable<Tuple<HashDigest<SHA256>, long>> IterateStateReferences(
            Guid chainId,
            Address address,
            long? highestIndex,
            long? lowestIndex,
            int? limit)
        {
            highestIndex = highestIndex ?? long.MaxValue;
            lowestIndex = lowestIndex ?? 0;

            if (highestIndex < lowestIndex)
            {
                var message =
                    $"highestIndex({highestIndex}) must be greater than or equal to " +
                    $"lowestIndex({lowestIndex})";
                throw new ArgumentException(
                    message,
                    nameof(highestIndex));
            }

            string collId = StateRefId(chainId);
            LiteCollection<StateRefDoc> coll = _db.GetCollection<StateRefDoc>(collId);
            string addressString = address.ToHex().ToLower();
            IEnumerable<StateRefDoc> stateRefs = coll.Find(
                Query.And(
                    Query.All("BlockIndex", Query.Descending),
                    Query.EQ("AddressString", addressString),
                    Query.Between("BlockIndex", lowestIndex, highestIndex)
                ), limit: limit ?? int.MaxValue
            );
            return stateRefs
                .Select(doc => new Tuple<HashDigest<SHA256>, long>(doc.BlockHash, doc.BlockIndex));
        }

        /// <inheritdoc/>
        public override void StoreStateReference(
            Guid chainId,
            IImmutableSet<Address> addresses,
            HashDigest<SHA256> hash,
            long index)
        {
            string collId = StateRefId(chainId);
            LiteCollection<StateRefDoc> coll = _db.GetCollection<StateRefDoc>(collId);
            IEnumerable<StateRefDoc> stateRefDocs = addresses
                .Select(addr => new StateRefDoc
                {
                    Address = addr,
                    BlockIndex = index,
                    BlockHash = hash,
                })
                .Where(doc => !coll.Exists(d => d.Id == doc.Id));
            coll.InsertBulk(stateRefDocs);
            coll.EnsureIndex("AddressString");
            coll.EnsureIndex("BlockIndex");
        }

        /// <inheritdoc/>
        public override void ForkStateReferences<T>(
            Guid sourceChainId,
            Guid destinationChainId,
            Block<T> branchPoint)
        {
            string srcCollId = StateRefId(sourceChainId);
            string dstCollId = StateRefId(destinationChainId);
            LiteCollection<StateRefDoc> srcColl = _db.GetCollection<StateRefDoc>(srcCollId),
                                        dstColl = _db.GetCollection<StateRefDoc>(dstCollId);

            dstColl.InsertBulk(srcColl.Find(Query.LTE("BlockIndex", branchPoint.Index)));

            if (!dstColl.Exists(_ => true) && CountIndex(sourceChainId) < 1)
            {
                throw new ChainIdNotFoundException(
                    sourceChainId,
                    "The source chain to be forked does not exist."
                );
            }

            dstColl.EnsureIndex("AddressString");
            dstColl.EnsureIndex("BlockIndex");
        }

        /// <inheritdoc/>
        public override IEnumerable<KeyValuePair<Address, long>> ListTxNonces(Guid chainId)
        {
            var collectionId = TxNonceId(chainId);
            LiteCollection<BsonDocument> collection = _db.GetCollection<BsonDocument>(collectionId);
            foreach (BsonDocument doc in collection.FindAll())
            {
                if (doc.TryGetValue("_id", out BsonValue id) && id.IsBinary)
                {
                    var address = new Address(id.AsBinary);
                    if (doc.TryGetValue("v", out BsonValue v) && v.IsInt64)
                    {
                        if (v.AsInt64 > 0)
                        {
                            yield return new KeyValuePair<Address, long>(address, v.AsInt64);
                        }
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override long GetTxNonce(Guid chainId, Address address)
        {
            var collectionId = TxNonceId(chainId);
            LiteCollection<BsonDocument> collection = _db.GetCollection<BsonDocument>(collectionId);
            var docId = new BsonValue(address.ToByteArray());
            BsonDocument doc = collection.FindById(docId);
            return doc is null ? 0 : (doc.TryGetValue("v", out BsonValue v) ? v.AsInt64 : 0);
        }

        /// <inheritdoc/>
        public override void IncreaseTxNonce(Guid chainId, Address signer, long delta = 1)
        {
            long nextNonce = GetTxNonce(chainId, signer) + delta;
            var collectionId = TxNonceId(chainId);
            LiteCollection<BsonDocument> collection = _db.GetCollection<BsonDocument>(collectionId);
            var docId = new BsonValue(signer.ToByteArray());
            collection.Upsert(docId, new BsonDocument() { ["v"] = new BsonValue(nextNonce) });
        }

        /// <inheritdoc/>
        public override long CountTransactions()
        {
            // FIXME: This implementation is too inefficient.  Fortunately, this method seems
            // unused (except for unit tests).  If this is never used why should we maintain
            // this?  This is basically only for making TransactionSet<T> class to implement
            // IDictionary<TxId, Transaction<T>>.Count property, which is never used either.
            // We'd better to refactor all such things so that unnecessary APIs are gone away.
            return IterateTransactionIds().LongCount();
        }

        /// <inheritdoc/>
        public override long CountBlocks()
        {
            _blockLock.EnterReadLock();
            try
            {
                return _db.FileStorage.Find("block/").Count();
            }
            finally
            {
                _blockLock.ExitReadLock();
            }
        }

        public void Dispose()
        {
            _db?.Dispose();
            _memoryStream?.Dispose();
            _root.Dispose();
        }

        internal static Guid ParseChainId(string chainIdString) =>
            new Guid(ByteUtil.ParseHex(chainIdString));

        internal static string FormatChainId(Guid chainId) =>
            ByteUtil.Hex(chainId.ToByteArray());

        internal override RawBlock? GetRawBlock(HashDigest<SHA256> blockHash)
        {
            _blockLock.EnterUpgradeableReadLock();
            try
            {
                LiteFileInfo file = _db.FileStorage.FindById(BlockFileId(blockHash));
                if (file is null)
                {
                    return null;
                }

                _blockLock.EnterWriteLock();
                try
                {
                    using (var stream = new MemoryStream())
                    {
                        DownloadFile(file, stream);
                        stream.Seek(0, SeekOrigin.Begin);

                        var formatter = new BencodexFormatter<RawBlock>();
                        return (RawBlock)formatter.Deserialize(stream);
                    }
                }
                finally
                {
                    _blockLock.ExitWriteLock();
                }
            }
            finally
            {
                _blockLock.ExitUpgradeableReadLock();
            }
        }

        private static void CreateDirectoryRecursively(IFileSystem fs, UPath path)
        {
            if (!fs.DirectoryExists(path))
            {
                CreateDirectoryRecursively(fs, path.GetDirectory());
                fs.CreateDirectory(path);
            }
        }

        private UPath TxPath(TxId txid)
        {
            string idHex = txid.ToHex();
            return UPath.Root / idHex.Substring(0, 2) / idHex.Substring(2);
        }

        private string BlockFileId(HashDigest<SHA256> blockHash)
        {
            return $"block/{blockHash}";
        }

        private string BlockStateFileId(HashDigest<SHA256> blockHash)
        {
            return $"state/{blockHash}";
        }

        private string StateRefId(Guid chainId)
        {
            return $"{StateRefIdPrefix}{FormatChainId(chainId)}";
        }

        private string TxNonceId(Guid chainId)
        {
            return $"{TxNonceIdPrefix}{FormatChainId(chainId)}";
        }

        private void UploadFile(string fileId, string filename, byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                stream.Seek(0, SeekOrigin.Begin);
                _db.FileStorage.Upload(fileId, filename, stream);
            }
        }

        private void DownloadFile(LiteFileInfo file, Stream stream)
        {
            file.CopyTo(stream);

            if (stream.Length > file.Length)
            {
                stream.SetLength(file.Length);
            }
        }

        private LiteCollection<HashDoc> IndexCollection(Guid chainId)
        {
            return _db.GetCollection<HashDoc>($"{IndexColPrefix}{FormatChainId(chainId)}");
        }

        internal class StateRefDoc
        {
            public string AddressString { get; set; }

            public long BlockIndex { get; set; }

            public string BlockHashString { get; set; }

            [BsonId]
            public string Id => AddressString + BlockHashString;

            [BsonIgnore]
            public Address Address
            {
                get
                {
                    return new Address(AddressString);
                }

                set
                {
                    AddressString = value.ToHex().ToLower();
                }
            }

            [BsonIgnore]
            public HashDigest<SHA256> BlockHash
            {
                get
                {
                    return HashDigest<SHA256>.FromString(BlockHashString);
                }

                set
                {
                    BlockHashString = value.ToString();
                }
            }
        }

        private class HashDoc
        {
            public long Id { get; set; }

            public HashDigest<SHA256> Hash { get; set; }
        }

        private class StagedTxIdDoc
        {
            public long Id { get; set; }

            public TxId TxId { get; set; }
        }
    }
}
