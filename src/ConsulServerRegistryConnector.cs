using Consul;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DarkRift.Server.Plugins.ServerRegistryConnectors.Consul
{
    /// <summary>
    /// DarkRift ServerRegistryConnector plugin for Consul.
    /// </sumamry>
    public class ConsulServerRegistryConnector : ServerRegistryConnector
    {
        public override bool ThreadSafe => true;

        public override Version Version => new Version(1, 0, 0);

        /// <summary>
        /// The URL to set as the Consul health check for the server.
        /// </summary>
        private readonly string healthCheckUrl = "http://localhost:10666/health";

        /// <summary>
        /// The poll interval of the Consul health check for the server.
        /// </summary>
        private readonly TimeSpan healthCheckPollInterval = TimeSpan.FromMilliseconds(5000);

        /// <summary>
        /// The maximum time the Consul health check for the server can be failing for before the server is deregistered.
        /// </summary>
        /// <remarks>
        /// Minimuim 1m, granularity ~30 seconds.
        /// </remarks>
        private readonly TimeSpan healthCheckTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        ///     The client to connect to Consul via.
        /// </summary>
        private readonly ConsulClient client;

        /// <summary>
        ///     The ID of this server in Consul.
        /// </summary>
        private ushort id;          // TODO this should be accessible in the base plugin and not necessary to store here.

        /// <summary>
        ///     The servers known to us.
        /// </summary>
        private IEnumerable<ushort> knownServices = new HashSet<ushort>();

        public ConsulServerRegistryConnector(ServerRegistryConnectorLoadData pluginLoadData) : base(pluginLoadData)
        {
            client = new ConsulClient(configuration =>
            {
                if (pluginLoadData.Settings["consulAddress"] != null)
                    configuration.Address = new Uri(pluginLoadData.Settings["consulAddress"]);

                if (pluginLoadData.Settings["consulDatacenter"] != null)
                    configuration.Datacenter = pluginLoadData.Settings["consulDatacenter"];

                if (pluginLoadData.Settings["consulToken"] != null)
                    configuration.Token = pluginLoadData.Settings["consulToken"];
            });

            if (pluginLoadData.Settings["healthCheckUrl"] != null)
                healthCheckUrl = pluginLoadData.Settings["healthCheckUrl"];

            if (pluginLoadData.Settings["healthCheckPollIntervalMs"] != null)
                healthCheckPollInterval = TimeSpan.FromMilliseconds(int.Parse(pluginLoadData.Settings["healthCheckPollIntervalMs"]));

            if (pluginLoadData.Settings["healthCheckTimeoutMs"] != null)
                healthCheckTimeout = TimeSpan.FromMilliseconds(int.Parse(pluginLoadData.Settings["healthCheckTimeoutMs"]));
        }

        protected async Task FetchServices()
        {
            Logger.Trace($"Refreshing services (I'm server {id}).");

            QueryResult<Dictionary<string, AgentService>> result;
            try
            {
                // TODO this library doesn't seem to allow us to get only services with a passing health check
                result = await client.Agent.Services();
            }
            catch (Exception e)
            {
                Logger.Error("Failed to fetch services from Consul as an exception occurred.", e);
                return;
            }

            Dictionary<string, AgentService> services = result.Response;

            // Map to ushort IDs
            Dictionary<ushort, AgentService> parsedServices = services.ToDictionary(kv => ushort.Parse(kv.Key), kv => kv.Value);

            IEnumerable<ushort> joined, left;
            lock (knownServices)
            {
                joined = parsedServices.Keys.Except(knownServices);
                left = knownServices.Except(parsedServices.Keys);

                knownServices = parsedServices.Keys;
            }

            foreach (ushort joinedID in joined)
            {
                AgentService service = parsedServices[joinedID];
                string group = service.Tags.First().Substring(6);

                HandleServerJoin(joinedID, group, service.Address, (ushort)service.Port, service.Meta);
            }

            //TODO consider just a set method instead of/as well as join/leave
            foreach (ushort leftID in left)
                HandleServerLeave(leftID);
        }

        protected override void DeregisterServer()
        {
            DeregisterServerAsync().Wait();
        }

        private async Task DeregisterServerAsync()
        {
            try
            {
                await client.Agent.ServiceDeregister(id.ToString());
            }
            catch (Exception e)
            {
                Logger.Error("Failed to deregister server from Consul as an exception occurred.", e);
                return;
            }
        }

        //TODO in future, when supported by the core DarkRift libraries, this should proably be an async method
        protected override ushort RegisterServer(string group, string host, ushort port, IDictionary<string, string> properties)
        {
            return RegisterServerAsync(group, host, port, properties).GetAwaiter().GetResult();
        }

        private async Task<ushort> RegisterServerAsync(string group, string host, int port, IDictionary<string, string> properties)
        {
            id = await AllocateID();

            Logger.Trace("Registering server on Consul...");

            // TODO add configuration
            AgentServiceCheck healthCheck = new AgentServiceCheck()
            {
                HTTP = healthCheckUrl,
                Interval = healthCheckPollInterval,
                DeregisterCriticalServiceAfter = healthCheckTimeout
            };

            AgentServiceRegistration service = new AgentServiceRegistration
            {
                ID = id.ToString(),
                Name = "DarkRift Server (" + group + ")",
                Address = host,
                Port = port,
                Tags = new string[] { "group:" + group },
                Meta = properties,
                Check = healthCheck
            };

            try
            {
                await client.Agent.ServiceRegister(service);
            }
            catch (Exception e)
            {
                Logger.Error("Failed to register server with Consul as an exception occurred.", e);
                throw e;
            }

            // Start timers and get an initial list
            FetchServices().Wait();
            CreateTimer(10000, 10000, (_) => FetchServices().Wait());

            return id;
        }

        /// <summary>
        ///     Allocates a new ID from Consul.
        /// </summary>
        /// <returns>The ID allocated.</returns>
        private async Task<ushort> AllocateID()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Logger.Trace("Allocating a new ID on Consul, attempt: " + (attempt + 1));

                QueryResult<KVPair> result = await client.KV.Get("darkrift-2/next-id");
                KVPair kvPair = result.Response;

                if (kvPair != null)
                {
                    bool valid = ushort.TryParse(Encoding.UTF8.GetString(kvPair.Value, 0, kvPair.Value.Length), out ushort id);
                    if (!valid)
                        throw new InvalidOperationException("Failed to allocate ID as the stored next ID is not a valid ushort.");

                    kvPair.Value = Encoding.UTF8.GetBytes((id + 1).ToString());

                    WriteResult<bool> casResult;
                    try
                    {
                        casResult = await client.KV.CAS(kvPair);
                    }
                    catch (Exception e)
                    {
                        Logger.Error("Failed to perform CAS operation on Consul while updating ID field.", e);
                        throw e;
                    }

                    if (casResult.Response)
                        return id;
                }
                else
                {
                    // First in the cluster, we need to create the next-id field!
                    kvPair = new KVPair("darkrift-2/next-id")
                    {
                        Value = Encoding.UTF8.GetBytes("1")
                    };

                    WriteResult<bool> casResult;
                    try
                    {
                        casResult = await client.KV.CAS(kvPair);
                    }
                    catch (Exception e)
                    {
                        Logger.Error("Failed to perform CAS operation on Consul while creating ID field.", e);
                        throw e;
                    }

                    if (casResult.Response)
                        return 0;
                }
            }

            Logger.Error("Failed to allocate ID from Consul as the operation exceeded the maximum number of allowed attempts (10).");
            throw new InvalidOperationException("Failed to allocate ID from Consul as the operation exceeded the maximum number of allowed attempts (10).");
        }

        /// <summary>
        ///     Disposes of the client.
        /// </summary>
        /// <param name="disposing">If we are disopsing.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                client.Dispose();
        }
    }
}
