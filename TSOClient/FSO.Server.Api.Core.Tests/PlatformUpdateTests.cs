using System.Runtime.InteropServices;
using FSO.Server.Api.Core.Controllers;
using Xunit;

namespace FSO.Server.Api.Core.Tests
{
    /// <summary>
    /// Area (a): platform-aware update refusal. InitialConnectController only hands a client the (Windows)
    /// in-game update payload when it is a Windows client; every non-Windows RID must be refused. These
    /// tests pin the exact decision (IsWindowsClient) so CI blocks any regression that would re-advertise
    /// the win-x64 patch to a Linux/macOS client.
    /// </summary>
    public class PlatformUpdateTests
    {
        [Theory]
        [InlineData("win-x64")]   // the supported Windows client
        [InlineData("win-x86")]   // any win-* prefix
        [InlineData("WIN-X64")]   // case-insensitive
        [InlineData(null)]        // legacy client that sends no rid -> defaults to Windows (preserves old flow)
        [InlineData("")]          // legacy client, empty rid
        public void WindowsAndLegacyClients_GetTheUpdatePayload(string rid)
        {
            Assert.True(InitialConnectController.IsWindowsClient(rid),
                "Windows / legacy clients must be eligible for the in-game update url.");
        }

        [Theory]
        [InlineData("linux-x64")]
        [InlineData("osx-x64")]
        [InlineData("osx-arm64")]
        [InlineData("linux-arm64")]
        public void NonWindowsClients_AreNeverGivenTheWindowsPayload(string rid)
        {
            Assert.False(InitialConnectController.IsWindowsClient(rid),
                "Non-Windows RIDs must never be handed the win-x64 in-game update url.");
        }

        [Fact]
        public void CurrentRuntimeRid_IsWellFormed_AndMatchesWindowsDecision()
        {
            var rid = FSO.Common.FSOEnvironment.RID;
            Assert.Matches("^(win|linux|osx|unknown)-", rid);

            var onWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            Assert.Equal(onWindows, InitialConnectController.IsWindowsClient(rid));
        }
    }
}
