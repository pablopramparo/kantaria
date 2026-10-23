// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Kantor.Core.Guest;

/// <summary>Una interfaz de red donde se puede ofrecer el acceso de invitados.</summary>
public sealed record GuestInterface(string Name, IPAddress Address, bool HasGateway);

/// <summary>
/// Qué interfaces sirven para el acceso por LAN: IPv4 privada, activa y no virtual. Que una dirección sea privada
/// NO garantiza el aislamiento de internet (un reenvío de puertos del router la expondría): por eso el servidor
/// escucha solo en la dirección elegida y además rechaza cualquier cliente que no sea de una red privada.
/// </summary>
public static partial class GuestNetwork
{
    [GeneratedRegex(@"hyper-v|virtual|vethernet|vmware|vbox|wsl|docker|tap-|\btun\d*\b|hamachi|zerotier|tailscale|wireguard|openvpn|vpn|loopback|pseudo|bluetooth|miniport|npcap|teredo|isatap|6to4",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualName();

    /// <summary>10.0.0.0/8, 172.16.0.0/12 o 192.168.0.0/16.</summary>
    public static bool IsPrivateLan(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Un cliente aceptable: de la LAN privada o el propio equipo.</summary>
    public static bool IsLocalOrLanClient(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) || IsPrivateLan(address);
    }

    public static bool LooksVirtual(string name, string description) =>
        VirtualName().IsMatch(name) || VirtualName().IsMatch(description);

    /// <summary>
    /// Interfaces candidatas, con puerta de enlace primero. <paramref name="allowHyperVExternal"/> (solo el modo demo) admite
    /// el conmutador externo de Hyper-V, que en una PC con Hyper-V es la propia placa de red y por eso parece virtual.
    /// </summary>
    public static IReadOnlyList<GuestInterface> Candidates(bool allowHyperVExternal = false)
    {
        var result = new List<GuestInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || (LooksVirtual(nic.Name, nic.Description)
                        && !(allowHyperVExternal && nic.Name.Contains("External", StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var gateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (IsPrivateLan(unicast.Address))
                    {
                        result.Add(new GuestInterface(nic.Name, unicast.Address, gateway));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // sin lista de interfaces: no hay dónde ofrecer el acceso
        }

        return [.. result.OrderByDescending(i => i.HasGateway).ThenBy(i => i.Address.ToString(), StringComparer.Ordinal)];
    }

    /// <summary>La preferida si sigue disponible; si no, la primera con puerta de enlace; si no, la primera.</summary>
    public static GuestInterface? Choose(IReadOnlyList<GuestInterface> candidates, string? preferredAddress) =>
        candidates.FirstOrDefault(c => preferredAddress is not null && c.Address.ToString() == preferredAddress)
        ?? candidates.FirstOrDefault(c => c.HasGateway)
        ?? candidates.FirstOrDefault();
}
