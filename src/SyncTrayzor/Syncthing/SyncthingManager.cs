﻿using NLog;
using RestEase;
using SyncTrayzor.Services.Config;
using SyncTrayzor.Syncthing.ApiClient;
using SyncTrayzor.Syncthing.EventWatcher;
using SyncTrayzor.Syncthing.TransferHistory;
using SyncTrayzor.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SyncTrayzor.Syncthing.DebugFacilities;
using SyncTrayzor.Syncthing.Devices;
using SyncTrayzor.Syncthing.Folders;

namespace SyncTrayzor.Syncthing
{
    public interface ISyncthingManager : IDisposable
    {
        SyncthingState State { get; }
        bool IsDataLoaded { get; }
        event EventHandler DataLoaded;
        event EventHandler<SyncthingStateChangedEventArgs> StateChanged;
        event EventHandler<MessageLoggedEventArgs> MessageLogged;
        SyncthingConnectionStats TotalConnectionStats { get; }
        event EventHandler<ConnectionStatsChangedEventArgs> TotalConnectionStatsChanged;
        event EventHandler ProcessExitedWithError;

        string ExecutablePath { get; set; }
        string ApiKey { get; set; }
        Uri PreferredAddress { get; set; }
        Uri Address { get; set; }
        List<string> SyncthingCommandLineFlags { get; set; }
        IDictionary<string, string> SyncthingEnvironmentalVariables { get; set; }
        string SyncthingCustomHomeDir { get; set; }
        bool SyncthingDenyUpgrade { get; set; }
        SyncthingPriorityLevel SyncthingPriorityLevel { get; set; }
        bool SyncthingHideDeviceIds { get; set; }
        TimeSpan SyncthingConnectTimeout { get; set; }
        DateTime StartedTime { get; }
        DateTime LastConnectivityEventTime { get; }
        SyncthingVersionInformation Version { get; }
        ISyncthingFolderManager Folders { get; }
        ISyncthingDeviceManager Devices { get; }
        ISyncthingTransferHistory TransferHistory { get; }
        ISyncthingDebugFacilitiesManager DebugFacilities { get; }

        Task StartAsync();
        Task StopAsync();
        Task StopAndWaitAsync();
        Task RestartAsync();
        void Kill();
        void KillAllSyncthingProcesses();

        Task ScanAsync(string folderId, string subPath);
        Task ReloadIgnoresAsync(string folderId);
    }

    public class SyncthingManager : ISyncthingManager
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly SynchronizedEventDispatcher eventDispatcher;
        private readonly ISyncthingProcessRunner processRunner;
        private readonly ISyncthingApiClientFactory apiClientFactory;

        // This lock covers the eventWatcher, connectionsWatcher, apiClients, and the CTS
        private readonly object apiClientsLock = new object();
        private readonly ISyncthingEventWatcher eventWatcher;
        private readonly ISyncthingConnectionsWatcher connectionsWatcher;
        private readonly SynchronizedTransientWrapper<ISyncthingApiClient> apiClient;
        private readonly IFreePortFinder freePortFinder;
        private CancellationTokenSource apiAbortCts;

        private DateTime _startedTime;
        private readonly object startedTimeLock = new object();
        public DateTime StartedTime
        {
            get { lock (this.startedTimeLock) { return this._startedTime; } }
            set { lock (this.startedTimeLock) { this._startedTime = value; } }
        }

        private DateTime _lastConnectivityEventTime;
        private readonly object lastConnectivityEventTimeLock = new object();
        public DateTime LastConnectivityEventTime
        {
            get { lock (this.lastConnectivityEventTimeLock) { return this._lastConnectivityEventTime; } }
            private set { lock (this.lastConnectivityEventTimeLock) { this._lastConnectivityEventTime = value; } }
        }

        private readonly object stateLock = new object();
        private SyncthingState _state;
        public SyncthingState State
        {
            get { lock (this.stateLock) { return this._state; } }
            set { lock (this.stateLock) { this._state = value; } }
        }

        private SystemInfo systemInfo;

        public bool IsDataLoaded { get; private set; }
        public event EventHandler DataLoaded;
        public event EventHandler<SyncthingStateChangedEventArgs> StateChanged;
        public event EventHandler<MessageLoggedEventArgs> MessageLogged;

        private readonly object totalConnectionStatsLock = new object();
        private SyncthingConnectionStats _totalConnectionStats;
        public SyncthingConnectionStats TotalConnectionStats
        {
            get { lock (this.totalConnectionStatsLock) { return this._totalConnectionStats; } }
            set { lock (this.totalConnectionStatsLock) { this._totalConnectionStats = value; } }
        }
        public event EventHandler<ConnectionStatsChangedEventArgs> TotalConnectionStatsChanged;

        public event EventHandler ProcessExitedWithError;

        public string ExecutablePath { get; set; }
        public string ApiKey { get; set; }
        public Uri PreferredAddress { get; set; }
        public Uri Address { get; set; }
        public string SyncthingCustomHomeDir { get; set; }
        public List<string> SyncthingCommandLineFlags { get; set; } = new List<string>();
        public IDictionary<string, string> SyncthingEnvironmentalVariables { get; set; } = new Dictionary<string, string>();
        public bool SyncthingDenyUpgrade { get; set; }
        public SyncthingPriorityLevel SyncthingPriorityLevel { get; set; }
        public bool SyncthingHideDeviceIds { get; set; }
        public TimeSpan SyncthingConnectTimeout { get; set; }

        public SyncthingVersionInformation Version { get; private set; }

        private readonly SyncthingFolderManager _folders;
        public ISyncthingFolderManager Folders => this._folders;

        private readonly SyncthingDeviceManager _devices;
        public ISyncthingDeviceManager Devices => this._devices;

        private readonly ISyncthingTransferHistory _transferHistory;
        public ISyncthingTransferHistory TransferHistory => this._transferHistory;

        private SyncthingDebugFacilitiesManager _debugFacilities;
        public ISyncthingDebugFacilitiesManager DebugFacilities => this._debugFacilities;

        public SyncthingManager(
            ISyncthingProcessRunner processRunner,
            ISyncthingApiClientFactory apiClientFactory,
            ISyncthingEventWatcherFactory eventWatcherFactory,
            ISyncthingConnectionsWatcherFactory connectionsWatcherFactory,
            IFreePortFinder freePortFinder)
        {
            this.StartedTime = DateTime.MinValue;
            this.LastConnectivityEventTime = DateTime.MinValue;

            this.eventDispatcher = new SynchronizedEventDispatcher(this);
            this.processRunner = processRunner;
            this.apiClientFactory = apiClientFactory;
            this.freePortFinder = freePortFinder;

            this.apiClient = new SynchronizedTransientWrapper<ISyncthingApiClient>(this.apiClientsLock);

            this.eventWatcher = eventWatcherFactory.CreateEventWatcher(this.apiClient);
            this.eventWatcher.DeviceConnected += (o, e) => this.LastConnectivityEventTime = DateTime.UtcNow;
            this.eventWatcher.DeviceDisconnected += (o, e) => this.LastConnectivityEventTime = DateTime.UtcNow;
            this.eventWatcher.ConfigSaved += (o, e) => this.ReloadConfigDataAsync();
            this.eventWatcher.EventsSkipped += (o, e) => this.ReloadConfigDataAsync();

            this.connectionsWatcher = connectionsWatcherFactory.CreateConnectionsWatcher(this.apiClient);
            this.connectionsWatcher.TotalConnectionStatsChanged += (o, e) => this.OnTotalConnectionStatsChanged(e.TotalConnectionStats);

            this._folders = new SyncthingFolderManager(this.apiClient, this.eventWatcher, TimeSpan.FromMinutes(10));
            this._devices = new SyncthingDeviceManager(this.apiClient, this.eventWatcher);
            this._transferHistory = new SyncthingTransferHistory(this.eventWatcher, this._folders);
            this._debugFacilities = new SyncthingDebugFacilitiesManager(this.apiClient);

            this.processRunner.ProcessStopped += (o, e) => this.ProcessStopped(e.ExitStatus);
            this.processRunner.MessageLogged += (o, e) => this.OnMessageLogged(e.LogMessage);
            this.processRunner.ProcessRestarted += (o, e) => this.ProcessRestarted();
            this.processRunner.Starting += (o, e) => this.ProcessStarting();
        }

        public async Task StartAsync()
        {
            this.processRunner.Start();
            await this.StartClientAsync();
        }

        public async Task StopAsync()
        {
            var apiClient = this.apiClient.Value;
            if (this.State != SyncthingState.Running || apiClient == null)
                return;

            // Syncthing can stop so quickly that it doesn't finish sending the response to us
            try
            {
                await apiClient.ShutdownAsync();
            }
            catch (HttpRequestException)
            { }

            this.SetState(SyncthingState.Stopping);
        }

        public async Task StopAndWaitAsync()
        {
            var apiClient = this.apiClient.Value;
            if (this.State != SyncthingState.Running || apiClient == null)
                return;

            var tcs = new TaskCompletionSource<object>();
            EventHandler<SyncthingStateChangedEventArgs> stateChangedHandler = (o, e) =>
            {
                if (e.NewState == SyncthingState.Stopped)
                    tcs.TrySetResult(null);
                else if (e.NewState != SyncthingState.Stopping)
                    tcs.TrySetException(new Exception($"Failed to stop Syncthing: Went to state {e.NewState} instead"));
            };
            this.StateChanged += stateChangedHandler;

            // Syncthing can stop so quickly that it doesn't finish sending the response to us
            try
            {
                await apiClient.ShutdownAsync();
            }
            catch (HttpRequestException)
            { }

            this.SetState(SyncthingState.Stopping);

            await tcs.Task;
            this.StateChanged -= stateChangedHandler;
        }

        public async Task RestartAsync()
        {
            if (this.State != SyncthingState.Running)
                return;

            // Syncthing can stop so quickly that it doesn't finish sending the response to us
            try
            {
                await this.apiClient.Value.RestartAsync();
            }
            catch (HttpRequestException)
            {
            }
        }

        public void Kill()
        {
            this.processRunner.Kill();
            this.SetState(SyncthingState.Stopped);
        }

        public void KillAllSyncthingProcesses()
        {
            this.processRunner.KillAllSyncthingProcesses();
        }  

        public Task ScanAsync(string folderId, string subPath)
        {
            return this.apiClient.Value.ScanAsync(folderId, subPath);
        }

        public Task ReloadIgnoresAsync(string folderId)
        {
            return this._folders.ReloadIgnoresAsync(folderId);
        }

        private void SetState(SyncthingState state)
        {
            SyncthingState oldState;
            bool abortApi = false;
            lock (this.stateLock)
            {
                logger.Debug("Request to set state: {0} -> {1}", this._state, state);
                if (state == this._state)
                    return;

                oldState = this._state;
                // We really need a proper state machine here....
                // There's a race if Syncthing can't start because the database is locked by another process on the same port
                // In this case, we see the process as having failed, but the event watcher chimes in a split-second later with the 'Started' event.
                // This runs the risk of transitioning us from Stopped -> Starting -> Stopped -> Running, which is bad news for everyone
                // So, get around this by enforcing strict state transitions.
                if (this._state == SyncthingState.Stopped && state == SyncthingState.Running)
                    return;

                // Not entirely sure where this condition comes from...
                if (this._state == SyncthingState.Stopped && state == SyncthingState.Stopping)
                    return;

                if (this._state == SyncthingState.Running ||
                    (this._state == SyncthingState.Starting && state == SyncthingState.Stopped))
                    abortApi = true;

                logger.Debug("Setting state: {0} -> {1}", this._state, state);
                this._state = state;
            }

            if (abortApi)
            {
                logger.Debug("Aborting API clients");
                // StopApiClients acquires the correct locks, and aborts the CTS
                this.StopApiClients();
            }

            this.eventDispatcher.Raise(this.StateChanged, new SyncthingStateChangedEventArgs(oldState, state));
        }

        private async Task CreateApiClientAsync(CancellationToken cancellationToken)
        {
            logger.Debug("Starting API clients");
            var apiClient = await this.apiClientFactory.CreateCorrectApiClientAsync(this.Address, this.ApiKey, this.SyncthingConnectTimeout, cancellationToken);
            logger.Debug("Have the API client! It's {0}", apiClient.GetType().Name);

            this.apiClient.Value = apiClient;

            this.SetState(SyncthingState.Running);
        }

        private async Task StartClientAsync()
        {
            try
            {
                this.apiAbortCts = new CancellationTokenSource();
                await this.CreateApiClientAsync(this.apiAbortCts.Token);
                await this.LoadStartupDataAsync(this.apiAbortCts.Token);
                this.StartWatchers(this.apiAbortCts.Token);
            }
            catch (OperationCanceledException) // If Syncthing dies on its own, etc
            {
                logger.Info("StartClientAsync aborted");
            }
            catch (ApiException e)
            {
                var msg = $"RestEase Error. StatusCode: {e.StatusCode}. Content: {e.Content}. Reason: {e.ReasonPhrase}";
                logger.Error(msg, e);
                throw new SyncthingDidNotStartCorrectlyException(msg, e);
            }
            catch (HttpRequestException e)
            {
                var msg = $"HttpRequestException while starting Syncthing: {e.Message}";
                logger.Error(msg, e);
                throw new SyncthingDidNotStartCorrectlyException(msg, e);
            }
            catch (Exception e)
            {
                logger.Error("Error starting Syncthing API", e);
                throw;
            }
        }

        private void StartWatchers(CancellationToken cancellationToken)
        {
            // This is all synchronous, so it's safe to execute inside the lock
            lock (this.apiClientsLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (apiClient == null)
                    throw new InvalidOperationException("ApiClient must not be null");

                this.connectionsWatcher.Start();
                this.eventWatcher.Start();
            }
        }

        private void StopApiClients()
        {
            lock (this.apiClientsLock)
            {
                if (this.apiAbortCts != null)
                    this.apiAbortCts.Cancel();

                this.apiClient.UnsynchronizedValue = null;

                this.connectionsWatcher.Stop();
                this.eventWatcher.Stop();
            }
        }

        private async void ProcessStarting()
        {
            var port = this.freePortFinder.FindFreePort(this.PreferredAddress.Port);
            var uriBuilder = new UriBuilder(this.PreferredAddress);
            uriBuilder.Port = port;
            this.Address = uriBuilder.Uri;

            this.processRunner.ApiKey = this.ApiKey;
            this.processRunner.HostAddress = this.Address.ToString();
            this.processRunner.ExecutablePath = this.ExecutablePath;
            this.processRunner.CustomHomeDir = this.SyncthingCustomHomeDir;
            this.processRunner.CommandLineFlags = this.SyncthingCommandLineFlags;
            this.processRunner.EnvironmentalVariables = this.SyncthingEnvironmentalVariables;
            this.processRunner.DebugFacilities = this.DebugFacilities.DebugFacilities.Where(x => x.IsEnabled).Select(x => x.Name).ToList();
            this.processRunner.DenyUpgrade = this.SyncthingDenyUpgrade;
            this.processRunner.SyncthingPriorityLevel = this.SyncthingPriorityLevel;
            this.processRunner.HideDeviceIds = this.SyncthingHideDeviceIds;

            var isRestart = (this.State == SyncthingState.Restarting);
            this.SetState(SyncthingState.Starting);

            // Catch restart cases, and re-start the API
            // This isn't ideal, as we don't get to nicely propagate any exceptions to the UI
            if (isRestart)
            {
                try
                {
                    await this.StartClientAsync();
                }
                catch (SyncthingDidNotStartCorrectlyException)
                {
                    // We've already logged this
                }
            }
        }

        private void ProcessStopped(SyncthingExitStatus exitStatus)
        {
            this.SetState(SyncthingState.Stopped);
            if (exitStatus == SyncthingExitStatus.Error)
                this.OnProcessExitedWithError();
        }

        private void ProcessRestarted()
        {
            this.SetState(SyncthingState.Restarting);
        }

        private async Task LoadStartupDataAsync(CancellationToken cancellationToken)
        {
            logger.Debug("Startup Complete! Loading startup data");

            // There's a race where Syncthing died, and so we kill the API clients and set it to null,
            // but we still end up here, because threading.
            cancellationToken.ThrowIfCancellationRequested();
            var apiClient = this.apiClient.GetAsserted();

            var syncthingVersionTask = apiClient.FetchVersionAsync();
            var systemInfoTask = apiClient.FetchSystemInfoAsync();

            await Task.WhenAll(syncthingVersionTask, systemInfoTask);

            this.systemInfo = systemInfoTask.Result;
            var syncthingVersion = syncthingVersionTask.Result;

            this.Version = new SyncthingVersionInformation(syncthingVersion.Version, syncthingVersion.LongVersion);
            
            cancellationToken.ThrowIfCancellationRequested();

            var debugFacilitiesLoadTask = this._debugFacilities.LoadAsync(this.Version.ParsedVersion);
            var configDataLoadTask = this.LoadConfigDataAsync(this.systemInfo.Tilde, false, cancellationToken);

            await Task.WhenAll(debugFacilitiesLoadTask, configDataLoadTask);

            cancellationToken.ThrowIfCancellationRequested();
            
            this.StartedTime = DateTime.UtcNow;
            this.IsDataLoaded = true;
            this.OnDataLoaded();
        }

        private async Task LoadConfigDataAsync(string tilde, bool isReload, CancellationToken cancellationToken)
        {
            var apiClient = this.apiClient.GetAsserted();

            var config = await apiClient.FetchConfigAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (isReload)
            {
                await Task.WhenAll(this._folders.ReloadFoldersAsync(config, tilde, cancellationToken), this._devices.ReloadDevicesAsync(config, cancellationToken));
            }
            else
            {
                await Task.WhenAll(this._folders.LoadFoldersAsync(config, tilde, cancellationToken), this._devices.LoadDevicesAsync(config, cancellationToken));
            }
        }

        private async void ReloadConfigDataAsync()
        {
            // Shit. We don't know what state any of our folders are in. We'll have to poll them all....
            // Note that we're executing on the ThreadPool here: we don't have a Task route back to the main thread.
            // Any exceptions are ours to manage....

            // HttpRequestException, ApiException, and  OperationCanceledException are more or less expected: Syncthing could shut down
            // at any point

            try
            { 
                await this.LoadConfigDataAsync(this.systemInfo.Tilde, true, CancellationToken.None);
            }
            catch (HttpRequestException)
            { }
            catch (OperationCanceledException)
            { }
            catch (ApiException)
            { }
        }

        private void OnMessageLogged(string logMessage)
        {
            this.eventDispatcher.Raise(this.MessageLogged, new MessageLoggedEventArgs(logMessage));
        }

        private void OnTotalConnectionStatsChanged(SyncthingConnectionStats stats)
        {
            this.TotalConnectionStats = stats;
            this.eventDispatcher.Raise(this.TotalConnectionStatsChanged, new ConnectionStatsChangedEventArgs(stats));
        }

        private void OnDataLoaded()
        {
            this.eventDispatcher.Raise(this.DataLoaded);
        }

        private void OnProcessExitedWithError()
        {
            this.eventDispatcher.Raise(this.ProcessExitedWithError);
        }

        public void Dispose()
        {
            this.processRunner.Dispose();
            this.StopApiClients();
            this.eventWatcher.Dispose();
            this.connectionsWatcher.Dispose();
        }
    }
}
