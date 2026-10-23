// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Net.Sockets;
using System.Windows;
using Kantor.App.Guest;
using Kantor.App.Remote;
using Kantor.Core.Guest;
using Kantor.Core.Menu;

namespace Kantor.App;

/// <summary>
/// Acceso de invitados por LAN: un servidor HTTP local, apagado por omisión (y siempre al arrancar), con un QR que
/// lleva el token. Se enciende y apaga desde las tarjetas del menú y de la lista, o desde la pantalla grande del QR.
/// Los invitados solo buscan, eligen cantantes y agregan a la cola: todo pasa por <see cref="GuestService"/>, que
/// usa la biblioteca, los jugadores y la cola de siempre.
/// </summary>
public partial class MainWindow
{
    private readonly GuestAccess _guestAccess = new();
    private GuestService? _guestService;
    private GuestWebServer? _guestServer;
    private GuestCard? _menuGuestCard;
    private GuestCard? _listGuestCard;
    private bool[,]? _guestQr;
    private string? _guestMessage;
    private bool _guestBusy;

    private void InitGuest()
    {
        _menuGuestCard = new GuestCard(compact: false);
        _listGuestCard = new GuestCard(compact: true);
        Menu.GuestHost.Child = _menuGuestCard;
        SongList.GuestHost.Child = _listGuestCard;
        foreach (var card in new[] { _menuGuestCard, _listGuestCard })
        {
            card.ActivateRequested += (_, _) => _ = SetGuestAccess(true);
            card.EnlargeRequested += (_, _) => OpenGuestScreen();
        }

        Options.GuestInterfaceChanged += address =>
        {
            _prefs = _prefs with { GuestInterface = address };
            SaveSettings();
            if (_guestServer is not null)
            {
                _ = RestartGuestAccess(); // se reabre en la red elegida (con otro token)
            }

            RefreshOptions();
        };
        RefreshGuest();
    }

    private void RefreshGuest()
    {
        var language = _prefs.CommentLanguage;
        var active = _guestServer?.IsRunning == true;
        var view = new GuestView(active, active ? _guestServer!.BaseUrl : null, active ? _guestQr : null, _guestMessage);
        _menuGuestCard?.Update(language, view);
        _listGuestCard?.Update(language, view);
        if (GuestScreen.Visibility == Visibility.Visible)
        {
            GuestScreen.Show(language, view);
        }
    }

    private void OpenGuestScreen()
    {
        GuestScreen.Visibility = Visibility.Visible;
        RefreshGuest();
    }

    private void OnGuestBack(object? sender, EventArgs e)
    {
        GuestScreen.Visibility = Visibility.Collapsed;
        if (Menu.Visibility == Visibility.Visible)
        {
            Menu.Show(_prefs.CommentLanguage);
        }
    }

    private void OnGuestToggle(object? sender, EventArgs e) => _ = SetGuestAccess(_guestServer is null);

    private async Task RestartGuestAccess()
    {
        await SetGuestAccess(false);
        await SetGuestAccess(true);
    }

    /// <summary>Enciende o apaga el acceso. Apagar revoca el token y cierra el puerto antes de volver.</summary>
    private async Task SetGuestAccess(bool on)
    {
        if (_guestBusy)
        {
            return;
        }

        _guestBusy = true;
        var language = _prefs.CommentLanguage;
        try
        {
            if (!on)
            {
                var server = _guestServer;
                _guestServer = null;
                _guestQr = null;
                if (server is not null)
                {
                    await server.DisposeAsync();
                }

                _guestMessage = null;
                return;
            }

            if (_guestServer is not null || _controller is null)
            {
                return;
            }

            var choice = GuestNetwork.Choose(GuestNetwork.Candidates(Demo.DemoMode.Enabled), _prefs.GuestInterface);
            if (choice is null)
            {
                _guestMessage = GuestTexts.NoNetwork(language);
                return;
            }

            _guestService ??= new GuestService(_catalog, _controller, new WindowInvoker(Dispatcher), new WpfCoverProvider(),
                new GuestAvatarSource(), () => _prefs.CommentLanguage);
            var started = new GuestWebServer(_guestService, _guestAccess, new GuestServerOptions { Address = choice.Address });
            started.Log += message => Diag.Mark("invitados: " + message);
            try
            {
                await started.StartAsync();
                _guestQr = GuestQr.Modules(started.JoinUrl!);
                if (Demo.DemoMode.Enabled)
                {
                    Diag.Mark("demo: enlace de invitados " + started.JoinUrl); // solo en demo: el enlace lleva la clave
                }

                _guestServer = started;
                _guestMessage = null;
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidOperationException)
            {
                await started.DisposeAsync();
                _guestMessage = GuestTexts.Failed(language, e.Message);
            }
        }
        finally
        {
            _guestBusy = false;
            RefreshGuest();
        }
    }

    /// <summary>Al cerrar Kantaria: revoca el token y libera el puerto (sin colgar el cierre más de unos segundos).</summary>
    private void StopGuest()
    {
        var server = _guestServer;
        _guestServer = null;
        _guestAccess.Revoke();
        if (server is not null)
        {
            Task.Run(async () => await server.DisposeAsync()).Wait(TimeSpan.FromSeconds(4));
        }
    }
}
