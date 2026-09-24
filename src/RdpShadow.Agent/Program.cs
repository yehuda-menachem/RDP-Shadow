using System.ServiceProcess;
using RdpShadow.Agent;

// Two roles in one exe:
//   (no args)  Windows service, SYSTEM, session 0: applies machine settings, keeps a helper alive in the active session.
//   --helper   runs as the logged-on user: owns the clipboard and talks to the app (only a process in the
//              user's session can read or write that user's clipboard).
if (args is ["--helper"])
{
    await Helper.RunAsync();
    return;
}
ServiceBase.Run(new AgentService());
