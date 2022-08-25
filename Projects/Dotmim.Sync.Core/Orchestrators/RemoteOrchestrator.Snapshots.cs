﻿using Dotmim.Sync.Args;
using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Dotmim.Sync
{
    public partial class RemoteOrchestrator : BaseOrchestrator
    {


        /// <summary>
        /// Get a snapshot
        /// </summary>
        public virtual async Task<(long RemoteClientTimestamp, BatchInfo ServerBatchInfo, DatabaseChangesSelected DatabaseChangesSelected)>
            GetSnapshotAsync(ScopeInfo sScopeInfo, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            var context = new SyncContext(Guid.NewGuid(), sScopeInfo.Name);

            try
            {
                await using var runner = await this.GetConnectionAsync(context, SyncMode.NoTransaction, SyncStage.ScopeLoading, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                long remoteClientTimestamp;
                BatchInfo serverBatchInfo;
                DatabaseChangesSelected databaseChangesSelected;

                (context, remoteClientTimestamp, serverBatchInfo, databaseChangesSelected) =
                    await this.InternalGetSnapshotAsync(sScopeInfo, context, runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return (remoteClientTimestamp, serverBatchInfo, databaseChangesSelected);

            }
            catch (Exception ex)
            {
                throw GetSyncError(context, ex);
            }
        }


        /// <summary>
        /// Get a snapshot
        /// </summary>
        internal virtual async Task<(SyncContext context, long RemoteClientTimestamp, BatchInfo ServerBatchInfo, DatabaseChangesSelected DatabaseChangesSelected)>
            InternalGetSnapshotAsync(ScopeInfo sScopeInfo, SyncContext context, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                await using var runner = await this.GetConnectionAsync(context, SyncMode.NoTransaction, SyncStage.ScopeLoading, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                // Get context or create a new one
                var changesSelected = new DatabaseChangesSelected();

                BatchInfo serverBatchInfo = null;
                if (string.IsNullOrEmpty(this.Options.SnapshotsDirectory))
                    return (context, 0, null, changesSelected);

                //Direction set to Download
                context.SyncWay = SyncWay.Download;

                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();

                // Get Schema from remote provider if no schema passed from args
                if (sScopeInfo.Schema == null)
                    (context, sScopeInfo, _) = await this.InternalEnsureScopeInfoAsync(context, sScopeInfo.Setup, false, runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                // When we get the changes from server, we create the batches if it's requested by the client
                // the batch decision comes from batchsize from client
                var (rootDirectory, nameDirectory) = await this.InternalGetSnapshotDirectoryPathAsync(sScopeInfo.Name, context.Parameters, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(rootDirectory))
                {
                    var directoryFullPath = Path.Combine(rootDirectory, nameDirectory);

                    // if no snapshot present, just return null value.
                    if (Directory.Exists(directoryFullPath))
                    {
                        // Serialize on disk.
                        var jsonConverter = new Serialization.JsonConverter<BatchInfo>();

                        var summaryFileName = Path.Combine(directoryFullPath, "summary.json");

                        using (var fs = new FileStream(summaryFileName, FileMode.Open, FileAccess.Read))
                        {
                            serverBatchInfo = await jsonConverter.DeserializeAsync(fs).ConfigureAwait(false);
                        }

                        // Create the schema changeset
                        var changesSet = new SyncSet();

                        // Create a Schema set without readonly columns, attached to memory changes
                        foreach (var table in sScopeInfo.Schema.Tables)
                        {
                            CreateChangesTable(sScopeInfo.Schema.Tables[table.TableName, table.SchemaName], changesSet);

                            // Get all stats about this table
                            var bptis = serverBatchInfo.BatchPartsInfo.SelectMany(bpi => bpi.Tables.Where(t =>
                            {
                                var sc = SyncGlobalization.DataSourceStringComparison;

                                var sn = t.SchemaName == null ? string.Empty : t.SchemaName;
                                var otherSn = table.SchemaName == null ? string.Empty : table.SchemaName;

                                return table.TableName.Equals(t.TableName, sc) && sn.Equals(otherSn, sc);

                            }));

                            if (bptis != null)
                            {
                                // Statistics
                                var tableChangesSelected = new TableChangesSelected(table.TableName, table.SchemaName)
                                {
                                    // we are applying a snapshot where it can't have any deletes, obviously
                                    Upserts = bptis.Sum(bpti => bpti.RowsCount)
                                };

                                if (tableChangesSelected.Upserts > 0)
                                    changesSelected.TableChangesSelected.Add(tableChangesSelected);
                            }


                        }
                        serverBatchInfo.SanitizedSchema = changesSet;
                    }
                }
                if (serverBatchInfo == null)
                    return (context, 0, null, changesSelected);


                await runner.CommitAsync().ConfigureAwait(false);

                return (context, serverBatchInfo.Timestamp, serverBatchInfo, changesSelected);
            }
            catch (Exception ex)
            {
                throw GetSyncError(context, ex);
            }
        }


        public virtual Task<BatchInfo> CreateSnapshotAsync(SyncSetup setup = null, SyncParameters syncParameters = null,
            DbConnection connection = default, DbTransaction transaction = default,
            CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
            => CreateSnapshotAsync(SyncOptions.DefaultScopeName, setup, syncParameters, connection, transaction, cancellationToken, progress);

        /// <summary>
        /// Create a snapshot, based on the Setup object. 
        /// </summary>
        /// <param name="syncParameters">if not parameters are found in the SyncContext instance, will use thes sync parameters instead</param>
        /// <returns>Instance containing all information regarding the snapshot</returns>
        public virtual async Task<BatchInfo> CreateSnapshotAsync(string scopeName, SyncSetup setup = null, SyncParameters syncParameters = null,
            DbConnection connection = default, DbTransaction transaction = default,
            CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            var context = new SyncContext(Guid.NewGuid(), scopeName);

            try
            {
                await using var runner = await this.GetConnectionAsync(context, SyncMode.WithTransaction, SyncStage.SnapshotCreating, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (string.IsNullOrEmpty(this.Options.SnapshotsDirectory) || this.Options.BatchSize <= 0)
                    throw new SnapshotMissingMandatariesOptionsException();

                // check parameters
                // If context has no parameters specified, and user specifies a parameter collection we switch them
                if ((context.Parameters == null || context.Parameters.Count <= 0) && syncParameters != null && syncParameters.Count > 0)
                    context.Parameters = syncParameters;

                // 1) Get Schema from remote provider
                ScopeInfo sScopeInfo;
                bool shouldProvision;
                (context, sScopeInfo, shouldProvision) = await this.InternalEnsureScopeInfoAsync(context, setup, false, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                // If we just have create the server scope, we need to provision it
                if (shouldProvision)
                {
                    // 2) Provision
                    var provision = SyncProvision.TrackingTable | SyncProvision.StoredProcedures | SyncProvision.Triggers;

                    (context, _) = await this.InternalProvisionAsync(sScopeInfo, context, false, provision, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    // Write scopes locally
                    (context, sScopeInfo) = await this.InternalSaveScopeInfoAsync(sScopeInfo, context, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);
                }

                // 4) Getting the most accurate timestamp
                long remoteClientTimestamp;
                (context, remoteClientTimestamp) = await this.InternalGetLocalTimestampAsync(context,
                    runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                // 5) Create the snapshot with
                BatchInfo batchInfo;

                (context, batchInfo) = await this.InternalCreateSnapshotAsync(sScopeInfo, context, remoteClientTimestamp,
                    runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return batchInfo;
            }
            catch (Exception ex)
            {
                throw GetSyncError(context, ex);
            }

        }

        internal virtual async Task<(SyncContext context, BatchInfo batchInfo)>
            InternalCreateSnapshotAsync(ScopeInfo sScopeInfo, SyncContext context,
              long remoteClientTimestamp, DbConnection connection, DbTransaction transaction,
              CancellationToken cancellationToken, IProgress<ProgressArgs> progress = null)
        {
            if (Provider == null)
                throw new MissingProviderException(nameof(InternalCreateSnapshotAsync));

            await using var runner = await this.GetConnectionAsync(context, SyncMode.WithTransaction, SyncStage.SnapshotCreating, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            await this.InterceptAsync(new SnapshotCreatingArgs(context, sScopeInfo.Schema, this.Options.SnapshotsDirectory, this.Options.BatchSize, remoteClientTimestamp, this.Provider.CreateConnection(), null), progress, cancellationToken).ConfigureAwait(false);

            if (!Directory.Exists(this.Options.SnapshotsDirectory))
                Directory.CreateDirectory(this.Options.SnapshotsDirectory);

            var (rootDirectory, nameDirectory) = await this.InternalGetSnapshotDirectoryPathAsync(sScopeInfo.Name, context.Parameters, cancellationToken, progress).ConfigureAwait(false);

            // create local directory with scope inside
            if (!Directory.Exists(rootDirectory))
                Directory.CreateDirectory(rootDirectory);

            // Delete directory if already exists
            var directoryFullPath = Path.Combine(rootDirectory, nameDirectory);

            // Delete old version if exists
            if (Directory.Exists(directoryFullPath))
                Directory.Delete(directoryFullPath, true);

            BatchInfo serverBatchInfo;

            (context, serverBatchInfo, _) =
                    await this.InternalGetChangesAsync(sScopeInfo, context, true, null, null, Guid.Empty,
                    this.Provider.SupportsMultipleActiveResultSets,
                    rootDirectory, nameDirectory, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            // since we explicitely defined remote client timestamp to null, to get all rows, just reaffect here
            serverBatchInfo.Timestamp = remoteClientTimestamp;

            // Serialize on disk.
            var jsonConverter = new Serialization.JsonConverter<BatchInfo>();

            var summaryFileName = Path.Combine(directoryFullPath, "summary.json");

            using (var f = new FileStream(summaryFileName, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var bytes = await jsonConverter.SerializeAsync(serverBatchInfo).ConfigureAwait(false);
                f.Write(bytes, 0, bytes.Length);
            }

            await this.InterceptAsync(new SnapshotCreatedArgs(context, serverBatchInfo, this.Provider.CreateConnection(), null), progress, cancellationToken).ConfigureAwait(false);

            await runner.CommitAsync().ConfigureAwait(false);

            return (context, serverBatchInfo);
        }


    }
}
