// -----------------------------------------------------------------------
//  <copyright file="ReplicationInformation.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.NewClient.Abstractions;
using Raven.NewClient.Abstractions.Data;
using Raven.NewClient.Abstractions.Extensions;
using Raven.NewClient.Abstractions.Logging;
using Raven.NewClient.Abstractions.Replication;
using Raven.NewClient.Abstractions.Util;
using Raven.NewClient.Client.Connection.Async;
using Raven.NewClient.Client.Connection.Request;
using Raven.NewClient.Client.Data;
using Raven.NewClient.Client.Document;
using Raven.NewClient.Client.Extensions;
using Raven.NewClient.Json.Linq;

namespace Raven.NewClient.Client.Connection
{
    public class ReplicationInformer : ReplicationInformerBase<ServerClient>, IDocumentStoreReplicationInformer
    {
        private readonly object replicationLock = new object();

        private bool firstTime = true;

        private DateTime lastReplicationUpdate = DateTime.MinValue;

        private Task refreshReplicationInformationTask;

        public ReplicationInformer(DocumentConvention conventions, HttpJsonRequestFactory jsonRequestFactory)
            : base(conventions, jsonRequestFactory)
        {
        }

        /// <summary>
        /// Failover servers set manually in config file or when document store was initialized
        /// </summary>
        public ReplicationDestination[] FailoverServers { get; set; }

        public Task UpdateReplicationInformationIfNeededAsync(AsyncServerClient serverClient)
        {
            // Default database doesn't have replication topology endpoint
            if (MultiDatabase.GetRootDatabaseUrl(serverClient.Url) == serverClient.Url)
                return Task.CompletedTask;

            return UpdateReplicationInformationIfNeededInternalAsync(serverClient.Url, () => 
                AsyncHelpers.RunSync(() => 
                    serverClient.DirectGetReplicationDestinationsAsync(new OperationMetadata(serverClient.Url, serverClient.PrimaryCredentials, null))));
        }

        private Task UpdateReplicationInformationIfNeededInternalAsync(string url, Func<ReplicationDocumentWithClusterInformation> getReplicationDestinations)
        {
            if (Conventions.FailoverBehavior == FailoverBehavior.FailImmediately)
                return new CompletedTask();

            if (lastReplicationUpdate.AddMinutes(5) > SystemTime.UtcNow)
                return new CompletedTask();

            lock (replicationLock)
            {
                if (firstTime)
                {
                    var serverHash = ServerHash.GetServerHash(url);

                    var document = ReplicationInformerLocalCache.TryLoadReplicationInformationFromLocalCache(serverHash);
                    if (IsInvalidDestinationsDocument(document) == false)
                        UpdateReplicationInformationFromDocument(document);
                }

                firstTime = false;

                if (lastReplicationUpdate.AddMinutes(5) > SystemTime.UtcNow)
                    return new CompletedTask();

                var taskCopy = refreshReplicationInformationTask;
                if (taskCopy != null)
                    return taskCopy;

                return refreshReplicationInformationTask = Task.Factory.StartNew(() => 
                    RefreshReplicationInformationInternal(url, getReplicationDestinations)).
                    ContinueWith(task =>
                    {
                        if (task.Exception != null)
                            Log.ErrorException("Failed to refresh replication information", task.Exception);
                        refreshReplicationInformationTask = null;
                    });
            }
        }

        public override void ClearReplicationInformationLocalCache(ServerClient client)
        {
            var serverHash = ServerHash.GetServerHash(client.Url);
            ReplicationInformerLocalCache.ClearReplicationInformationFromLocalCache(serverHash);
        }

        protected override void UpdateReplicationInformationFromDocument(JsonDocument document)
        {
            var replicationDocument = document.DataAsJson.JsonDeserialization<ReplicationDocumentWithClusterInformation>();
            ReplicationDestinations = replicationDocument.Destinations.Select(x =>
            {
                var url = string.IsNullOrEmpty(x.ClientVisibleUrl) ? x.Url : x.ClientVisibleUrl;
                if (string.IsNullOrEmpty(url))
                    return null;
                if (x.CanBeFailover() == false) 
                    return null;
                if (string.IsNullOrEmpty(x.Database))
                    return new OperationMetadata(url, x.Username, x.Password, x.Domain, x.ApiKey, x.ClusterInformation);

                return new OperationMetadata(
                    MultiDatabase.GetRootDatabaseUrl(url) + "/databases/" + x.Database + "/",
                    x.Username,
                    x.Password,
                    x.Domain,
                    x.ApiKey,
                    x.ClusterInformation);
            })
                // filter out replication destination that don't have the url setup, we don't know how to reach them
                // so we might as well ignore them. Probably private replication destination (using connection string names only)
                .Where(x => x != null)
                .ToList();
            foreach (var replicationDestination in ReplicationDestinations)
            {
                FailureCounter value;
                if (FailureCounters.FailureCounts.TryGetValue(replicationDestination.Url, out value))
                    continue;
                FailureCounters.FailureCounts[replicationDestination.Url] = new FailureCounter();
            }

            if (replicationDocument.ClientConfiguration != null)
                Conventions.UpdateFrom(replicationDocument.ClientConfiguration);
        }

        protected override string GetServerCheckUrl(string baseUrl)
        {
            return baseUrl + "/replication/topology?check-server-reachable";
        }

        public void RefreshReplicationInformation(AsyncServerClient serverClient)
        {
            RefreshReplicationInformationInternal(serverClient.Url, () => AsyncHelpers.RunSync(() => serverClient.DirectGetReplicationDestinationsAsync(new OperationMetadata(serverClient.Url, serverClient.PrimaryCredentials, null))));
        }

        public override void RefreshReplicationInformation(ServerClient serverClient)
        {
            RefreshReplicationInformationInternal(serverClient.Url, () => serverClient.DirectGetReplicationDestinations(new OperationMetadata(serverClient.Url, serverClient.PrimaryCredentials, null)));
        }

        private void RefreshReplicationInformationInternal(string url, Func<ReplicationDocumentWithClusterInformation> getReplicationDestinations)
        {
            lock (this)
            {
                var serverHash = ServerHash.GetServerHash(url);

                JsonDocument document;
                var fromFailoverUrls = false;

                try
                {
                    var replicationDestinations = getReplicationDestinations();
                    document = replicationDestinations == null ? null : RavenJObject.FromObject(replicationDestinations).ToJsonDocument();
                    FailureCounters.FailureCounts[url] = new FailureCounter(); // we just hit the master, so we can reset its failure count
                }
                catch (Exception e)
                {
                    Log.ErrorException("Could not contact master for new replication information", e);
                    document = ReplicationInformerLocalCache.TryLoadReplicationInformationFromLocalCache(serverHash);

                    if (document == null)
                    {
                        if (FailoverServers != null && FailoverServers.Length > 0) // try to use configured failover servers
                        {
                            var failoverServers = new ReplicationDocument { Destinations = new List<ReplicationDestination>() };

                            foreach (var failover in FailoverServers)
                            {
                                failoverServers.Destinations.Add(failover);
                            }

                            document = new JsonDocument
                                       {
                                           DataAsJson = RavenJObject.FromObject(failoverServers)
                                       };

                            fromFailoverUrls = true;
                        }
                    }
                }


                if (document == null)
                {
                    lastReplicationUpdate = SystemTime.UtcNow; // checked and not found
                    ReplicationDestinations.Clear(); // clear destinations that could be retrieved from local storage
                    return;
                }

                if (!fromFailoverUrls)
                    ReplicationInformerLocalCache.TrySavingReplicationInformationToLocalCache(serverHash, document);

                UpdateReplicationInformationFromDocument(document);

                lastReplicationUpdate = SystemTime.UtcNow;
            }
        }

        public override void Dispose()
        {
            base.Dispose();

            var replicationInformationTaskCopy = refreshReplicationInformationTask;
            if (replicationInformationTaskCopy != null)
            {
                try
                {
                    replicationInformationTaskCopy.Wait();
                }
                catch (Exception e)
                {
                    if(Log.IsWarnEnabled)
                        Log.WarnException("Failure in getting replication information during dispose", e);
                }
            }
    }
}}