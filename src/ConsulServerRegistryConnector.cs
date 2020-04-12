using Consul;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DarkRift.Server.Plugins.ServerRegistryConnectors.Consul
{
    public class ConsulServerRegistryConnector : ServerRegistryConnector
    {
        public override bool ThreadSafe => true;

        public override Version Version => new Version(1, 0, 0);

        /// <summary>
        ///     The ID of this server in Consul.
        /// </summary>
        private ushort id;

        /// <summary>
        ///     The servers know to us.
        /// </summary>
        private IEnumerable<ushort> knownServices = new HashSet<ushort>();

        /// <summary>
        ///     The client to connect to Consul via.
        /// </summary>
        private readonly ConsulClient client = new ConsulClient();

        public ConsulServerRegistryConnector(ServerRegistryConnectorLoadData pluginLoadData) : base(pluginLoadData)
        {
            // TODO Allow config of Consul client host/ports etc.
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
                // TODO better choice of exception here?
                throw new InvalidOperationException("Failed to fetch services from Consul.", e);
            }

            Dictionary<string, AgentService> services = result.Response;

            // Map to ushort IDs
            // TODO catch FormatException
            Dictionary<ushort, AgentService> parsedServices = services.ToDictionary(kv => ushort.Parse(kv.Key), kv => kv.Value);

            var joined = parsedServices.Keys.Except(knownServices);
            var left = knownServices.Except(parsedServices.Keys);

            // TODO how should we handle ourselves?
            foreach (ushort joinedID in joined)
            {
                if (joinedID != id)
                {
                    AgentService service = parsedServices[joinedID];
                    string group = service.Tags.First().Substring(6);

                    Logger.Trace($"Discovered server {joinedID} from group '{group}'.");

                    HandleServerJoin(joinedID, group, service.Address, (ushort)service.Port, service.Meta);
                }
            }

            //TODO consider just a set method instead of/as well as join/leave
            foreach (ushort leftID in left)
            {
                if (leftID != id)
                {
                    Logger.Trace($"Server {leftID} has left the system.");

                    HandleServerLeave(leftID);
                }
            }

            knownServices = parsedServices.Keys;
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
                // TODO better choice of exception here?
                throw new InvalidOperationException("Failed to deregister the service with Consul.", e);
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
                HTTP = "http://localhost:10666/health",
                Interval = TimeSpan.FromMilliseconds(5000),
                DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(60)       // Minimuim 1m, granularity ~30 seconds
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
                throw new InvalidOperationException("Failed to register the service with Consul.", e);
            }

            // Start timers and get an initial list
            FetchServices().Wait();
            CreateTimer(10000, 10000, (_) => {
                try
                {
                    FetchServices().Wait();
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to fetch services from Consul.", e);
                }
            });

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
                        throw new InvalidOperationException("Failed to perform CAS operation on Consul.", e);
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
                        throw new InvalidOperationException("Failed to perform CAS operation on Consul.", e);
                    }

                    if (casResult.Response)
                        return 0;
                }
            }

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
