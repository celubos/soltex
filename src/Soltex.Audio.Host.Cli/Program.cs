using Soltex.Audio.Host;
using Soltex.Audio.OwnedEndpoints;

try { return await AudioHostServer.RunAsync(args, () => new OwnedEndpointBackend()); }
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
{ Console.Error.WriteLine(error.Message); return 1; }
