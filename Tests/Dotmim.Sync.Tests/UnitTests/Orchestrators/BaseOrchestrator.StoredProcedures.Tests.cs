﻿using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.SqlServer;
using Dotmim.Sync.Tests.Core;
using Dotmim.Sync.Tests.Models;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Dotmim.Sync.Tests.UnitTests
{
    public partial class BaseOrchestratorTests
    {

        [Fact]
        public async Task BaseOrchestrator_StoredProcedure_ShouldCreate()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            // Create default table
            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var scopeName = "scope";

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product" })
            {
                StoredProceduresPrefix = "sp_",
                StoredProceduresSuffix = "_sp"
            };

            var remoteOrchestrator = new RemoteOrchestrator(sqlProvider, options);

            var scopeInfo = await remoteOrchestrator.GetServerScopeInfoAsync(scopeName, setup);

            await remoteOrchestrator.CreateStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges, false);

            Assert.True(await remoteOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges));

            // Adding a filter to check if stored procedures "with filters" are also generated
            setup.Filters.Add("Product", "ProductCategoryID", "SalesLT");

            // Create a new scope with this filter
            var scopeName2 = "scope2";
            var scopeInfo2 = await remoteOrchestrator.GetClientScopeInfoAsync(scopeName2, setup);

            await remoteOrchestrator.CreateStoredProcedureAsync(scopeInfo2, "Product", "SalesLT", DbStoredProcedureType.SelectChangesWithFilters, false);

            Assert.True(await remoteOrchestrator.ExistStoredProcedureAsync(scopeInfo2, "Product", "SalesLT", DbStoredProcedureType.SelectChangesWithFilters));

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }


        [Fact]
        public async Task BaseOrchestrator_StoredProcedure_ShouldOverwrite()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            // Create default table
            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var scopeName = "scope";

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product" })
            {
                StoredProceduresPrefix = "sp_",
                StoredProceduresSuffix = "_sp"
            };

            var localOrchestrator = new LocalOrchestrator(sqlProvider, options);

            var scopeInfo = await localOrchestrator.GetClientScopeInfoAsync(scopeName, setup);

            var storedProcedureSelectChanges = $"SalesLT.{setup.StoredProceduresPrefix}Product{setup.StoredProceduresSuffix}_changes";

            await localOrchestrator.CreateStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges, false);

            var assertOverWritten = false;

            localOrchestrator.On(new Action<StoredProcedureCreatingArgs>(args =>
            {
                assertOverWritten = true;
            }));

            await localOrchestrator.CreateStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges, true);

            Assert.True(assertOverWritten);

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }


        [Fact]
        public async Task BaseOrchestrator_StoredProcedure_ShouldNotOverwrite()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            // Create default table
            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var scopeName = "scope";

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product" })
            {
                StoredProceduresPrefix = "sp_",
                StoredProceduresSuffix = "_sp"
            };

            var localOrchestrator = new LocalOrchestrator(sqlProvider, options);

            var scopeInfo = await localOrchestrator.GetClientScopeInfoAsync(scopeName, setup);

            var storedProcedureSelectChanges = $"SalesLT.{setup.StoredProceduresPrefix}Product{setup.StoredProceduresSuffix}_changes";

            await localOrchestrator.CreateStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges, false);

            var assertOverWritten = false;

            localOrchestrator.On(new Action<StoredProcedureCreatingArgs>(args =>
            {
                assertOverWritten = true;
            }));

            await localOrchestrator.CreateStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges, false);

            Assert.False(assertOverWritten);

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }

        [Fact]
        public async Task BaseOrchestrator_StoredProcedure_Exists()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            // Create default table
            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var scopeName = "scope";

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product" })
            {
                StoredProceduresPrefix = "sp_",
                StoredProceduresSuffix = "_sp"
            };

            var localOrchestrator = new LocalOrchestrator(sqlProvider, options);

            var scopeInfo = await localOrchestrator.GetClientScopeInfoAsync(scopeName, setup);

            await localOrchestrator.CreateStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges, false);

            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges));
            Assert.False(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.UpdateRow));

            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }

        [Fact]
        public async Task BaseOrchestrator_StoredProcedures_ShouldCreate()
        {
            var dbName = HelperDatabase.GetRandomName("tcp_lo_");
            await HelperDatabase.CreateDatabaseAsync(ProviderType.Sql, dbName, true);

            var cs = HelperDatabase.GetConnectionString(ProviderType.Sql, dbName);
            var sqlProvider = new SqlSyncProvider(cs);

            // Create default table
            var ctx = new AdventureWorksContext((dbName, ProviderType.Sql, sqlProvider), true, false);
            await ctx.Database.EnsureCreatedAsync();

            var scopeName = "scope";

            var options = new SyncOptions();
            var setup = new SyncSetup(new string[] { "SalesLT.Product" })
            {
                StoredProceduresPrefix = "sp_",
                StoredProceduresSuffix = "_sp"
            };
            // Adding a filter to check if stored procedures "with filters" are also generated
            setup.Filters.Add("Product", "ProductCategoryID", "SalesLT");

            var localOrchestrator = new LocalOrchestrator(sqlProvider, options);

            var scopeInfo = await localOrchestrator.GetClientScopeInfoAsync(scopeName, setup);

            await localOrchestrator.CreateStoredProceduresAsync(scopeInfo, "Product", "SalesLT");

            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.BulkDeleteRows));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.BulkTableType));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.BulkUpdateRows));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.DeleteMetadata));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.DeleteRow));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.Reset));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChanges));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectInitializedChanges));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectRow));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.UpdateRow));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectChangesWithFilters));
            Assert.True(await localOrchestrator.ExistStoredProcedureAsync(scopeInfo, "Product", "SalesLT", DbStoredProcedureType.SelectInitializedChangesWithFilters));


            HelperDatabase.DropDatabase(ProviderType.Sql, dbName);
        }



    }
}
