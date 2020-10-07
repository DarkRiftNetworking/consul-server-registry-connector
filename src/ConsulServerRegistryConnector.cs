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
            {
                healthCheckTimeout = TimeSpan.FromMilliseconds(int.Parse(pluginLoadData.Settings["healthCheckTimeoutMs"]));
                if (healthCheckTimeout < TimeSpan.FromMinutes(1))
                    throw new InvalidOperationException("healthCheckTimeout property cannot be less than 1 minute.");
            }
        }

        /// <summary>
        /// Pulls the current services from Consul and updates the server with any changes to that list.
        /// </summary>
        /// <returns>A task object for the operation.</returns>
        protected async Task FetchServices()
        {
            Logger.Trace($"Refreshing services (I'm server {RemoteServerManager.ServerID}).");

            // Query Consul for the current list of services
            // TODO the library we use doesn't seem to allow us to get only services with a passing health check
            QueryResult<Dictionary<string, AgentService>> result;
            try
            {
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

            // Get all known sevices
            IEnumerable<ushort> knownServices = RemoteServerManager.GetAllGroups().SelectMany(g => g.GetAllRemoteServers()).Select(s => s.ID);

            // Diff the current services aginst the known services
            IEnumerable<ushort> joined, left;
            joined = parsedServices.Keys.Where(k => !knownServices.Contains(k));
            left = knownServices.Where(k => !parsedServices.ContainsKey(k));

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

        /// <summary>
        /// Deregisters the server with Consul.
        /// </summary>
        /// <returns>A task object for the operation.</returns>
        private async Task DeregisterServerAsync()
        {
            try
            {
                await client.Agent.ServiceDeregister(RemoteServerManager.ServerID.ToString());
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

        /// <summary>
        /// Registers the server with Consul.
        /// </summary>
        /// <param name="group">The group the server is in.</param>
        /// <param name="host">The advertised host property of the server.</param>
        /// <param name="port">The advertised port property of the server.</param>
        /// <param name="properties">Additional properties supplied by the server.</param>
        /// <returns>A task object for the operation.</returns>
        private async Task<ushort> RegisterServerAsync(string group, string host, int port, IDictionary<string, string> properties)
        {
            ushort id = await AllocateID();

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
        /// <returns>A task object for the operation with the ID allocated.</returns>
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
