# Consul ServerRegistryConnector
This is a DarkRift ServerRegistryConnector plugin for [Consul](https://www.consul.io/). It allows DarkRift to discover other servers in a system to connect to.

## Getting Started
### Building
_This plugin assumes you are using the [DarkRift CLI](https://github.com/DarkRiftNetworking/darkrift-cli) tool. If you do not use this, you need to update the paths to the DR DLLs in `src\DarkRift.Server.Plugins.ServerRegistryConnectors.Consul.csproj`._

1. To build this plugin clone the repository locally and run
```bash
darkrift pull 2.8.1 -s Core --pro
dotnet publish src
```

2. Copy the following files from `src\bin\Debug\netstandard2.0\publish` to your server's plugins folder:
- `Consul.dll`
- `Newtonsoft.Json.dll`
- `DarkRift.Server.Plugins.ServerRegistryConnectors.Consul.dll`
- `DarkRift.Server.Plugins.ServerRegistryConnectors.Consul.pdb`

3. Add the following to your server configuration:
```xml
<serverRegistry advertisedHost="localhost">
  <serverRegistryConnector type="ConsulServerRegistryConnector">
    <settings consulAddress="http://localhost:8500" />
  </serverRegistryConnector>
</serverRegistry>
```

### Running a cluster (for dev)
1. Download Consul from [here](https://www.consul.io/).
2. Run a Consul agent in dev mode (makes it a bit easier for simple testing, don't use dev mode in production!)
```bash
consul agent -dev
```
3. Start your servers on the same machine
```bash
darkrift run
```

### Tips
When dealing with multiple DR servers using the same configuration file it can be good to make use of some of DR's command line tricks. Firstly, setting the port and [health check](https://www.darkriftnetworking.com/DarkRift2/Docs/2.8.1/advanced/health_checks.html) ports as a [configuration variables](https://www.darkriftnetworking.com/DarkRift2/Docs/2.8.1/advanced/configuration_variables.html) allows you to specify them from the command line which helps start multiple servers without port clashes. If you do change the health check port you will need to set the `healthCheckUrl` setting on this plugin to point at the new location so Consul checks to correct URL.

Also, it can be neater to create multiple server configuration files and to specify them as the first argument on the CLI to switch which server is being created.

```bash
darkrift run WorldServer.config -port=4296 -healthCheckPort=10666
```

_You do not need the DarkRift CLI tool to do this, but I'm using it in this example._

## Settings
The following settings are exposed and can be configured using the `<settings>` XML element in the DarkRift configuration file:
```xml
<serverRegistryConnector type="ConsulServerRegistryConnector">
  <settings consulAddress="http://localhost:8500" />
</serverRegistryConnector>
```
- `consulAddress` - The URI to connect to the Consul agent.
- `consulDatacenter` - Datacenter to provide with each request. If not provided, the default agent datacenter is used.
- `consulToken` - Token is used to provide an ACL token which overrides the agent's default token. This ACL token is used for every request by clients created using this configuration.
- `healthCheckUrl` - The URL to set as the Consul health check for the server. Defaults to `http://localhost:10666/health`.
- `healthCheckPollIntervalMs` - The poll interval of the Consul health check for the server, in milliseconds. Defaults to `5000ms`.
- `healthCheckTimeoutMs` - The maximum time the Consul health check for the server can be failing for before the server is deregistered, in milliseconds. The Minimum value is `60000ms`, and granularity ~`30000ms`. Defaults to `60000ms`.
- `serviceName` - The service name to register as in Consul. Defaults to `darkrift`.
