using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// The service is a Linux host agent: it manipulates /etc/passwd, /etc/shadow, PAM and POSIX ACLs.
[assembly: SupportedOSPlatform("linux")]
[assembly: InternalsVisibleTo("FileManager.Core.Tests")]
