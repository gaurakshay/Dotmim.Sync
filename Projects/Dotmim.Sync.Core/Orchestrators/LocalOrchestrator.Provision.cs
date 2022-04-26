﻿using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Serialization;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Dotmim.Sync
{
    public partial class LocalOrchestrator : BaseOrchestrator
    {

        /// <summary>
        /// Provision the local database based on the scope info parameter.
        /// Scope info parameter should contains Schema and Setup properties
        /// </summary>
        public virtual async Task<ScopeInfo> ProvisionAsync(ServerScopeInfo serverScopeInfo, SyncProvision provision = default, bool overwrite = true, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {

            if (serverScopeInfo.Schema == null)
                throw new Exception($"No Schema in your server scope {serverScopeInfo.Name}");

            if (serverScopeInfo.Schema == null)
                throw new Exception($"No Setup in your server scope {serverScopeInfo.Name}");

            var scopeInfo = await this.GetClientScopeAsync(serverScopeInfo.Name, null, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            scopeInfo.Setup = serverScopeInfo.Setup;
            scopeInfo.Schema = serverScopeInfo.Schema;
            scopeInfo.Version = serverScopeInfo.Version;

            return await this.ProvisionAsync(scopeInfo, provision, overwrite, connection, transaction, cancellationToken, progress);
        }

        /// <summary>
        /// Provision the local database based on the scope info parameter.
        /// Scope info parameter should contains Schema and Setup properties
        /// </summary>
        public virtual async Task<ScopeInfo> ProvisionAsync(IScopeInfo scopeInfo, SyncProvision provision = default, bool overwrite = true, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                if (scopeInfo.Schema == null)
                    throw new Exception($"No Schema in your scopeInfo {scopeInfo.Name}");

                if (scopeInfo.Schema == null)
                    throw new Exception($"No Setup in your scopeInfo {scopeInfo.Name}");

                await using var runner = await this.GetConnectionAsync(scopeInfo.Name, SyncMode.Writing, SyncStage.Provisioning, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                // Check incompatibility with the flags
                if (provision.HasFlag(SyncProvision.ServerHistoryScope) || provision.HasFlag(SyncProvision.ServerScope))
                    throw new InvalidProvisionForLocalOrchestratorException();

                // 2) Provision
                if (provision == SyncProvision.None)
                    provision = SyncProvision.Table | SyncProvision.StoredProcedures | SyncProvision.Triggers | SyncProvision.TrackingTable;

                await this.InternalProvisionAsync(scopeInfo, overwrite, provision, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                // Write scopes locally
                await this.InternalSaveScopeAsync(scopeInfo, DbScopeType.Client, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return scopeInfo as ScopeInfo;
            }
            catch (Exception ex)
            {
                throw GetSyncError(scopeInfo.Name, ex);
            }
        }

        public virtual Task<ScopeInfo> ProvisionAsync(SyncSetup setup = null, SyncProvision provision = default, bool overwrite = true, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null) =>
            ProvisionAsync(SyncOptions.DefaultScopeName, setup, provision, overwrite, connection, transaction, cancellationToken, progress);

        /// <summary>
        /// Provision the local database based on the setup in parameter
        /// The schema should exists in your local database
        /// </summary>
        public virtual async Task<ScopeInfo> ProvisionAsync(string scopeName, SyncSetup setup = null, SyncProvision provision = default, bool overwrite = true, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                await using var runner = await this.GetConnectionAsync(scopeName, SyncMode.Writing, SyncStage.Provisioning, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                // Check incompatibility with the flags
                if (provision.HasFlag(SyncProvision.ServerHistoryScope) || provision.HasFlag(SyncProvision.ServerScope))
                    throw new InvalidProvisionForLocalOrchestratorException();

                // get client scope and create tables / row if needed
                var scopeInfo = await this.GetClientScopeAsync(scopeName, setup, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                if (scopeInfo.Schema == null || !scopeInfo.Schema.HasTables || !scopeInfo.Schema.HasColumns)
                    throw new Exception($"No Schema from the Client scope {scopeName}");

                // 2) Provision
                if (provision == SyncProvision.None)
                    provision = SyncProvision.Table | SyncProvision.StoredProcedures | SyncProvision.Triggers | SyncProvision.TrackingTable;

                await this.InternalProvisionAsync(scopeInfo, overwrite, provision, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                // Write scopes locally
                await this.InternalSaveScopeAsync(scopeInfo, DbScopeType.Client, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return scopeInfo as ScopeInfo;
            }
            catch (Exception ex)
            {
                throw GetSyncError(scopeName, ex);
            }
        }


        /// <summary>
        /// Deprovision the orchestrator database based on the schema argument, and the provision enumeration
        /// </summary>
        /// <param name="schema">Schema to be deprovisioned from the database managed by the orchestrator, through the provider.</param>
        /// <param name="provision">Provision enumeration to determine which components to deprovision</param>
        public virtual async Task<bool> DeprovisionAsync(IScopeInfo scopeInfo, SyncProvision provision = default, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                if (provision == default)
                    provision = SyncProvision.ClientScope | SyncProvision.StoredProcedures | SyncProvision.Triggers | SyncProvision.TrackingTable;

                await using var runner = await this.GetConnectionAsync(scopeInfo.Name, SyncMode.Writing, SyncStage.Deprovisioning, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                if (scopeInfo.Schema == null || !scopeInfo.Schema.HasTables || !scopeInfo.Schema.HasColumns)
                    return false;

                var isDeprovisioned = await InternalDeprovisionAsync(scopeInfo, provision, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return isDeprovisioned;
            }
            catch (Exception ex)
            {
                throw GetSyncError(scopeInfo.Name, ex);
            }
        }



        /// <summary>
        /// Deprovision the orchestrator database based on the setup argument, and the provision enumeration
        /// </summary>
        public virtual Task<bool> DeprovisionAsync(SyncSetup setup, SyncProvision provision = default, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
            => DeprovisionAsync(SyncOptions.DefaultScopeName, setup, provision, connection, transaction, cancellationToken, progress);

        /// <summary>
        /// Deprovision the orchestrator database based on the setup argument, and the provision enumeration
        /// </summary>
        public virtual async Task<bool> DeprovisionAsync(string scopeName, SyncSetup setup, SyncProvision provision = default, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                if (provision == default)
                    provision = SyncProvision.ClientScope | SyncProvision.StoredProcedures | SyncProvision.Triggers | SyncProvision.TrackingTable;

                await using var runner = await this.GetConnectionAsync(scopeName, SyncMode.Writing, SyncStage.Deprovisioning, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                // Creating a fake scope info
                var scopeInfo = this.InternalCreateScope(scopeName, DbScopeType.Client, cancellationToken, progress);
                scopeInfo.Setup = setup;

                var isDeprovisioned = await InternalDeprovisionAsync(scopeInfo, provision, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return isDeprovisioned;
            }
            catch (Exception ex)
            {
                throw GetSyncError(scopeName, ex);
            }
        }


    }
}
