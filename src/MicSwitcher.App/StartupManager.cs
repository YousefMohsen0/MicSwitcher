using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace MicSwitcher.App;

/// <summary>
/// "Start with Windows" implemented as a Task Scheduler logon task that runs with the highest privileges.
/// Unlike a registry Run key, this starts elevated at logon without a UAC prompt. Creating or deleting the
/// task needs administrator rights, so those calls trigger one UAC prompt.
/// </summary>
public static class StartupManager
{
    public const string TaskName = "MicSwitcher";

    /// <summary>Command-line switch used by the scheduled task so the app starts hidden in the tray.</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>Creates or removes the logon task. Returns null on success, otherwise an error message.</summary>
    public static string? SetEnabled(bool enabled)
    {
        string? xmlPath = null;
        try
        {
            string arguments;
            if (enabled)
            {
                xmlPath = Path.Combine(Path.GetTempPath(), $"MicSwitcher-task-{Guid.NewGuid():N}.xml");
                File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
                arguments = $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F";
            }
            else
            {
                arguments = $"/Delete /TN \"{TaskName}\" /F";
            }

            var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
            {
                Verb = "runas", // one UAC prompt; required to register a "highest privileges" task
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return "Could not start the Task Scheduler tool.";
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return null;
            }

            // Deleting a task that does not exist is not an error for our purposes.
            return enabled ? $"Task Scheduler reported error code {process.ExitCode}." : null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The administrator prompt was cancelled, so the startup setting was not changed.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            if (xmlPath is not null)
            {
                try { File.Delete(xmlPath); } catch (IOException) { }
            }
        }
    }

    private static string BuildTaskXml()
    {
        string user = Escape(WindowsIdentity.GetCurrent().Name);
        string exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");
        if (string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "MicSwitcher is running through 'dotnet run'. Publish it and start MicSwitcher.exe to enable \"Start with Windows\".");
        }

        string exe = Escape(exePath);
        string workDir = Escape(Path.GetDirectoryName(exePath) ?? string.Empty);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts MicSwitcher in the background at logon with the highest privileges.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{BackgroundArgument}</Arguments>
                  <WorkingDirectory>{workDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
