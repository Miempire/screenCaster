using ScreenCast.Server;

// The whole server is one process: an RFC5389 Binding responder and the SCSG signaling
// service, each on its own UDP socket. TURN is not implemented yet.
return await ServerHost.RunAsync(args).ConfigureAwait(false);