using System.Windows;
using BrainX.Core.Models;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>One browser window per peer: a second click brings the open one
    /// forward instead of stacking copies.</summary>
    private readonly Dictionary<string, PeerBrowseWindow> _peerBrowsers = new(StringComparer.Ordinal);

    private void OpenPeerBrowser(PeerInfo peer)
    {
        if (string.IsNullOrWhiteSpace(peer.BrainAddress)) return;
        if (!_network.IsConnected)
        {
            MessageBox.Show(this,
                "Join the network first. · เข้าร่วมเครือข่ายก่อน",
                "Browse peer · เปิดดูสมอง", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_peerBrowsers.TryGetValue(peer.BrainAddress, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var w = new PeerBrowseWindow(_network, peer.BrainAddress, peer.DisplayName) { Owner = this };
        _peerBrowsers[peer.BrainAddress] = w;
        w.Closed += (_, _) => _peerBrowsers.Remove(peer.BrainAddress);
        w.Show();
    }
}
