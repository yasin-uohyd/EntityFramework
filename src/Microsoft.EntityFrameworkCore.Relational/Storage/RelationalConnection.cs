// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Utilities;
using Microsoft.Extensions.Logging;
using IsolationLevel = System.Data.IsolationLevel;
#if NET451
using System.Transactions;

#endif
namespace Microsoft.EntityFrameworkCore.Storage
{
    /// <summary>
    ///     <para>
    ///         Represents a connection with a relational database.
    ///     </para>
    ///     <para>
    ///         This type is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </summary>
    public abstract class RelationalConnection : IRelationalConnection
    {
        private readonly string _connectionString;
        private readonly LazyRef<DbConnection> _connection;
        private readonly bool _connectionOwned;
        private int _openedCount;
        private bool _openedInternally;
        private int? _commandTimeout;

        /// <summary>
        ///     Initializes a new instance of the <see cref="RelationalConnection" /> class.
        /// </summary>
        /// <param name="dependencies">Parameter object containing dependencies for this service. </param>
        protected RelationalConnection([NotNull] RelationalConnectionDependencies dependencies)
        {
            Check.NotNull(dependencies, nameof(dependencies));

            Dependencies = dependencies;

            var relationalOptions = RelationalOptionsExtension.Extract(dependencies.ContextOptions);
            
            _commandTimeout = relationalOptions.CommandTimeout;

            if (relationalOptions.Connection != null)
            {
                if (!string.IsNullOrWhiteSpace(relationalOptions.ConnectionString))
                {
                    throw new InvalidOperationException(RelationalStrings.ConnectionAndConnectionString);
                }

                _connection = new LazyRef<DbConnection>(() => relationalOptions.Connection);
                _connectionOwned = false;
            }
            else if (!string.IsNullOrWhiteSpace(relationalOptions.ConnectionString))
            {
                _connectionString = relationalOptions.ConnectionString;
                _connection = new LazyRef<DbConnection>(CreateDbConnection);
                _connectionOwned = true;
            }
            else
            {
                throw new InvalidOperationException(RelationalStrings.NoConnectionOrConnectionString);
            }
        }

        /// <summary>
        ///     The unique identifier for this connection.
        /// </summary>
        public virtual Guid ConnectionId { get; } = Guid.NewGuid();

        /// <summary>
        ///     Parameter object containing service dependencies.
        /// </summary>
        protected virtual RelationalConnectionDependencies Dependencies { get; }

        /// <summary>
        ///     Creates a <see cref="DbConnection" /> to the database.
        /// </summary>
        /// <returns> The connection. </returns>
        protected abstract DbConnection CreateDbConnection();

        /// <summary>
        ///     Gets the logger to write to.
        /// </summary>
        protected virtual ILogger Logger => Dependencies.Logger;

        /// <summary>
        ///     Gets the diagnostic source.
        /// </summary>
        protected virtual DiagnosticSource DiagnosticSource => Dependencies.DiagnosticSource;

        /// <summary>
        ///     Gets the connection string for the database.
        /// </summary>
        public virtual string ConnectionString => _connectionString ?? _connection.Value.ConnectionString;

        /// <summary>
        ///     Gets the underlying <see cref="System.Data.Common.DbConnection" /> used to connect to the database.
        /// </summary>
        public virtual DbConnection DbConnection => _connection.Value;

        /// <summary>
        ///     Gets the current transaction.
        /// </summary>
        public virtual IDbContextTransaction CurrentTransaction { get; [param: CanBeNull] protected set; }

        /// <summary>
        ///     Gets the timeout for executing a command against the database.
        /// </summary>
        public virtual int? CommandTimeout
        {
            get { return _commandTimeout; }
            set
            {
                if (value.HasValue
                    && value < 0)
                {
                    throw new ArgumentException(RelationalStrings.InvalidCommandTimeout);
                }

                _commandTimeout = value;
            }
        }

        /// <summary>
        ///     Begins a new transaction.
        /// </summary>
        /// <returns> The newly created transaction. </returns>
        [NotNull]
        public virtual IDbContextTransaction BeginTransaction() => BeginTransaction(IsolationLevel.Unspecified);

        /// <summary>
        ///     Asynchronously begins a new transaction.
        /// </summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken" /> to observe while waiting for the task to complete.</param>
        /// <returns>
        ///     A task that represents the asynchronous operation. The task result contains the newly created transaction.
        /// </returns>
        [NotNull]
        public virtual async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default(CancellationToken))
            => await BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken);

        /// <summary>
        ///     Begins a new transaction.
        /// </summary>
        /// <param name="isolationLevel"> The isolation level to use for the transaction. </param>
        /// <returns> The newly created transaction. </returns>
        [NotNull]
        public virtual IDbContextTransaction BeginTransaction(IsolationLevel isolationLevel)
        {
            if (CurrentTransaction != null)
            {
                throw new InvalidOperationException(RelationalStrings.TransactionAlreadyStarted);
            }

            Open();

            return BeginTransactionWithNoPreconditions(isolationLevel);
        }

        /// <summary>
        ///     Asynchronously begins a new transaction.
        /// </summary>
        /// <param name="isolationLevel"> The isolation level to use for the transaction. </param>
        /// <param name="cancellationToken">A <see cref="CancellationToken" /> to observe while waiting for the task to complete.</param>
        /// <returns>
        ///     A task that represents the asynchronous operation. The task result contains the newly created transaction.
        /// </returns>
        [NotNull]
        public virtual async Task<IDbContextTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (CurrentTransaction != null)
            {
                throw new InvalidOperationException(RelationalStrings.TransactionAlreadyStarted);
            }

            await OpenAsync(cancellationToken);

            return BeginTransactionWithNoPreconditions(isolationLevel);
        }

        private IDbContextTransaction BeginTransactionWithNoPreconditions(IsolationLevel isolationLevel)
        {
            Logger.LogDebug(
                RelationalEventId.BeginningTransaction,
                isolationLevel,
                il => RelationalStrings.RelationalLoggerBeginningTransaction(il.ToString("G")));

            CurrentTransaction
                = new RelationalTransaction(
                    this,
                    DbConnection.BeginTransaction(isolationLevel),
                    Logger,
                    DiagnosticSource,
                    transactionOwned: true);

            return CurrentTransaction;
        }

        /// <summary>
        ///     Specifies an existing <see cref="DbTransaction" /> to be used for database operations.
        /// </summary>
        /// <param name="transaction"> The transaction to be used. </param>
        public virtual IDbContextTransaction UseTransaction(DbTransaction transaction)
        {
            if (transaction == null)
            {
                if (CurrentTransaction != null)
                {
                    CurrentTransaction = null;
                }
            }
            else
            {
                if (CurrentTransaction != null)
                {
                    throw new InvalidOperationException(RelationalStrings.TransactionAlreadyStarted);
                }

                Open();

                CurrentTransaction = new RelationalTransaction(this, transaction, Logger, DiagnosticSource, transactionOwned: false);
            }

            return CurrentTransaction;
        }

        /// <summary>
        ///     Commits all changes made to the database in the current transaction.
        /// </summary>
        public virtual void CommitTransaction()
        {
            if (CurrentTransaction == null)
            {
                throw new InvalidOperationException(RelationalStrings.NoActiveTransaction);
            }

            CurrentTransaction.Commit();
        }

        /// <summary>
        ///     Discards all changes made to the database in the current transaction.
        /// </summary>
        public virtual void RollbackTransaction()
        {
            if (CurrentTransaction == null)
            {
                throw new InvalidOperationException(RelationalStrings.NoActiveTransaction);
            }

            CurrentTransaction.Rollback();
        }

        /// <summary>
        ///     Opens the connection to the database.
        /// </summary>
        public virtual void Open()
        {
            CheckForAmbientTransactions();

            if (_connection.Value.State == ConnectionState.Broken)
            {
                _connection.Value.Close();
            }

            if (_connection.Value.State != ConnectionState.Open)
            {
                Logger.LogDebug(
                    RelationalEventId.OpeningConnection,
                    new
                    {
                        _connection.Value.Database,
                        _connection.Value.DataSource
                    },
                    state =>
                        RelationalStrings.RelationalLoggerOpeningConnection(
                            state.Database,
                            state.DataSource));

                var startTimestamp = Stopwatch.GetTimestamp();
                var instanceId = Guid.NewGuid();
                DiagnosticSource.WriteConnectionOpening(_connection.Value,
                    ConnectionId,
                    instanceId,
                    startTimestamp,
                    async: false);

                try
                {
                    _connection.Value.Open();

                    var currentTimestamp = Stopwatch.GetTimestamp();
                    DiagnosticSource.WriteConnectionOpened(_connection.Value, 
                        ConnectionId,
                        instanceId,
                        startTimestamp, 
                        currentTimestamp,
                        async: false);
                }
                catch (Exception e)
                {
                    var currentTimestamp = Stopwatch.GetTimestamp();
                    DiagnosticSource.WriteConnectionError(_connection.Value, 
                        ConnectionId, 
                        e,
                        instanceId,
                        startTimestamp,
                        currentTimestamp,
                        async: false);
                    throw;
                }

                if (_openedCount == 0)
                {
                    _openedInternally = true;
                    _openedCount++;
                }
            }
            else
            {
                _openedCount++;
            }
        }

        /// <summary>
        ///     Asynchronously opens the connection to the database.
        /// </summary>
        /// <param name="cancellationToken">
        ///     A <see cref="CancellationToken" /> to observe while waiting for the task to complete.
        /// </param>
        /// <returns> A task that represents the asynchronous operation. </returns>
        public virtual async Task OpenAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckForAmbientTransactions();

            if (_connection.Value.State == ConnectionState.Broken)
            {
                _connection.Value.Close();
            }

            if (_connection.Value.State != ConnectionState.Open)
            {
                Logger.LogDebug(
                    RelationalEventId.OpeningConnection,
                    new
                    {
                        _connection.Value.Database,
                        _connection.Value.DataSource
                    },
                    state =>
                        RelationalStrings.RelationalLoggerOpeningConnection(
                            state.Database,
                            state.DataSource));

                var startTimestamp = Stopwatch.GetTimestamp();
                var instanceId = Guid.NewGuid();
                DiagnosticSource.WriteConnectionOpening(_connection.Value,
                    ConnectionId,
                    instanceId,
                    startTimestamp,
                    async: true);

                try
                {
                    await _connection.Value.OpenAsync(cancellationToken);

                    var currentTimestamp = Stopwatch.GetTimestamp();
                    DiagnosticSource.WriteConnectionOpened(_connection.Value,
                        ConnectionId,
                        instanceId,
                        startTimestamp,
                        currentTimestamp,
                        async: true);
                }
                catch (Exception e)
                {
                    var currentTimestamp = Stopwatch.GetTimestamp();
                    DiagnosticSource.WriteConnectionError(_connection.Value,
                        ConnectionId,
                        e,
                        instanceId,
                        startTimestamp,
                        currentTimestamp,
                        async: true);
                    throw;
                }

                if (_openedCount == 0)
                {
                    _openedInternally = true;
                    _openedCount++;
                }
            }
            else
            {
                _openedCount++;
            }
        }

        private void CheckForAmbientTransactions()
        {
#if NET451
            if (Transaction.Current != null)
            {
                Logger.LogWarning(
                    RelationalEventId.AmbientTransactionWarning,
                    () => RelationalStrings.AmbientTransaction);
            }
#endif
        }

        /// <summary>
        ///     Closes the connection to the database.
        /// </summary>
        public virtual void Close()
        {
            if (_openedCount > 0
                && --_openedCount == 0
                && _openedInternally)
            {
                if (_connection.Value.State != ConnectionState.Closed)
                {
                    Logger.LogDebug(
                        RelationalEventId.ClosingConnection,
                        new
                        {
                            _connection.Value.Database,
                            _connection.Value.DataSource
                        },
                        state =>
                            RelationalStrings.RelationalLoggerClosingConnection(
                                state.Database,
                                state.DataSource));

                    var startTimestamp = Stopwatch.GetTimestamp();
                    var instanceId = Guid.NewGuid();
                    DiagnosticSource.WriteConnectionClosing(_connection.Value,
                        ConnectionId,
                        instanceId,
                        startTimestamp,
                        async: false);

                    try
                    {
                        _connection.Value.Close();

                        var currentTimestamp = Stopwatch.GetTimestamp();
                        DiagnosticSource.WriteConnectionClosed(_connection.Value,
                            ConnectionId,
                            instanceId,
                            startTimestamp,
                            currentTimestamp,
                            async: false);
                    }
                    catch (Exception e)
                    {
                        var currentTimestamp = Stopwatch.GetTimestamp();
                        DiagnosticSource.WriteConnectionError(_connection.Value,
                            ConnectionId,
                            e,
                            instanceId,
                            startTimestamp,
                            currentTimestamp,
                            async: false);
                        throw;
                    }
                }
                _openedInternally = false;
            }
        }

        /// <summary>
        ///     Gets a value indicating whether the multiple active result sets feature is enabled.
        /// </summary>
        public virtual bool IsMultipleActiveResultSetsEnabled => false;

        /// <summary>
        ///     Gets or sets the active cursor.
        /// </summary>
        public virtual IValueBufferCursor ActiveCursor { get; set; }

        void IResettableService.Reset() => Dispose();

        /// <summary>
        ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public virtual void Dispose()
        {
            CurrentTransaction?.Dispose();

            if (_connectionOwned && _connection.HasValue)
            {
                _connection.Value.Dispose();
                _connection.Reset(CreateDbConnection);
                _openedCount = 0;
            }
        }
    }
}
