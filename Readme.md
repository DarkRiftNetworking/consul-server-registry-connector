# Consul ServerRegistryConnector
This is a DarkRift ServerRegistryConnector plugin for Consul. It allows DarkRift to discover other servers in a system to connect to.

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
- `healthCheckTimeoutMs` - The maximum time the Consul health check for the server can be failing for before the server is deregistered, in milliseconds. The Minimum value is `60000ms`, and granularity ~`30000ms`. Defaults to `60000ms`
