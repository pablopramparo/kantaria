// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kantor.App.Characters;
using Kantor.App.Pixel;
using Kantor.App.Results;
using Kantor.Core.Characters;
using Kantor.Core.Menu;

namespace Kantor.App.Players;

// Personajes propios: lista, borrado y el modal para cargar uno (cuerpo entero + cara recortada o aparte).
public partial class PlayersView
{
    /// <summary>Lo que se va armando en el modal "Cargar personaje".</summary>
    private sealed class CharacterDraft
    {
        public BitmapSource? Body;
        public string? BodyFile;
        public BitmapSource? Face;
        public string? FaceFile;
        public SquareCrop Crop;

        /// <summary>De dónde sale la cara: la imagen aparte si hay y, si no, el cuerpo entero.</summary>
        public BitmapSource? CropSource => Face ?? Body;
    }

    private FaceChoice? _facePicker;

    /// <summary>
    /// Abre la grilla de avatares sobre toda la pantalla, dentro de la composición (no es un popup: nunca queda
    /// fuera de la ventana). Las caras que no entran se recorren con scroll; el teclado se mueve por la grilla.
    /// </summary>
    private void ShowFacePicker(FaceChoice choice)
    {
        _facePicker = choice;
        FacePanel.Children.Clear();
        Button? current = null;
        for (var i = 0; i < choice.Faces.Count; i++)
        {
            var index = i;
            var face = PixelUi.Avatar(choice.Faces[i], PlayerColors.For(i == choice.SelectedIndex ? 3 : 1), 72);
            var button = new Button
            {
                Content = face,
                Margin = new Thickness(4),
                Padding = new Thickness(4),
                Tag = $"face{i}",
            };
            button.SetResourceReference(StyleProperty, "PixelTileButton");
            button.GotKeyboardFocus += (_, _) => button.BringIntoView();
            button.Click += (_, _) =>
            {
                CloseFacePicker();
                choice.Pick(index);
            };
            if (i == choice.SelectedIndex)
            {
                current = button;
            }

            FacePanel.Children.Add(button);
        }

        ContentPanel.IsEnabled = false;
        TabsPanel.IsEnabled = false;
        ModalLayer.IsEnabled = false;
        FaceLayer.Visibility = Visibility.Visible;
        FaceScroll.ScrollToTop();
        var start = current ?? FindFirstFocusable(FacePanel);
        Dispatcher.BeginInvoke(() =>
        {
            start?.Focus();
            start?.BringIntoView();
        }, DispatcherPriority.Input);
    }

    private void OnFaceCloseClick(object sender, RoutedEventArgs e) => CloseFacePicker();

    /// <summary>Cierra la grilla sin elegir (Esc, o después de elegir) y devuelve el foco al selector.</summary>
    private void CloseFacePicker()
    {
        var choice = _facePicker;
        _facePicker = null;
        FaceLayer.Visibility = Visibility.Collapsed;
        FacePanel.Children.Clear();
        ModalLayer.IsEnabled = true;
        ContentPanel.IsEnabled = ModalLayer.Visibility != Visibility.Visible && _draft is null;
        TabsPanel.IsEnabled = ContentPanel.IsEnabled;
        choice?.Focus();
    }

    private CharacterDraft? _draft;
    private string? _confirmDeleteCharacter;

    private void BuildCharactersSection(PlayersModel m)
    {
        var heading = PixelUi.Note(T("c.title"), 20);
        heading.Foreground = PixelUi.Brush("GoldBrush");
        heading.Margin = new Thickness(0, 22, 0, 6);
        ContentPanel.Children.Add(heading);

        var add = PixelUi.Action("charNew", T("c.new"), () =>
        {
            _draft = new CharacterDraft();
            _focusKey = "cBody";
            BuildContent();
        }, 20, Focus);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 0, 0, 8);
        ContentPanel.Children.Add(add);

        var mine = m.Catalog.All.Where(c => c.IsUser).ToList();
        if (mine.Count == 0)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("c.none"), 18));
        }

        foreach (var character in mine)
        {
            var current = character;
            var face = PixelUi.Avatar(m.PortraitOf(current.Id), PlayerColors.For(1), 48);
            face.VerticalAlignment = VerticalAlignment.Center;
            var confirming = _confirmDeleteCharacter == current.Id;
            var delete = PixelUi.Action($"cdel{current.Id}", confirming ? T("p.confirm") : T("c.delete"), () =>
            {
                if (_confirmDeleteCharacter != current.Id)
                {
                    _confirmDeleteCharacter = current.Id;
                    BuildContent();
                    return;
                }

                _confirmDeleteCharacter = null;
                try
                {
                    m.UserCharacters.Delete(current.Id);
                    m.Catalog.SetUserCharacters(m.UserCharacters.Load());
                    _message = T("c.deleted");
                    DataChanged?.Invoke();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _message = e.Message;
                }

                BuildContent();
            }, 20, Focus);
            delete.Margin = new Thickness(10, 0, 0, 0);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(face);
            row.Children.Add(delete);
            ContentPanel.Children.Add(row);
        }

        ContentPanel.Children.Add(PixelUi.Note(T("c.help")));
    }

    private void BuildCharacterModal(PlayersModel m, CharacterDraft draft)
    {
        ModalPanel.Children.Clear();
        ModalPanel.Children.Add(new TextBlock
        {
            Text = T("c.newtitle"),
            FontFamily = PixelUi.Pixel,
            FontSize = 38,
            Foreground = PixelUi.Brush("GoldBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });

        if (_message is not null)
        {
            // el aviso de ContentPanel queda tapado por el modal: se repite acá
            var note = PixelUi.Note(_message, 18);
            note.Foreground = PixelUi.Brush("GoldBrush");
            ModalPanel.Children.Add(note);
        }

        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        pickers.Children.Add(PixelUi.Action("cBody", T("c.body"), () => PickImage(draft, face: false), 20, Focus));
        var faceButton = PixelUi.Action("cFace", T("c.face"), () => PickImage(draft, face: true), 20, Focus);
        faceButton.Margin = new Thickness(10, 0, 0, 0);
        pickers.Children.Add(faceButton);
        if (draft.Face is not null)
        {
            var fromBody = PixelUi.Action("cFromBody", T("c.facefrombody"), () =>
            {
                draft.Face = null;
                draft.FaceFile = null;
                ResetCrop(draft);
                _focusKey = "cFromBody";
                BuildContent();
            }, 20, Focus);
            fromBody.Margin = new Thickness(10, 0, 0, 0);
            fromBody.IsEnabled = draft.Body is not null;
            pickers.Children.Add(fromBody);
        }

        ModalPanel.Children.Add(pickers);

        var status = $"{T("c.body")} {draft.BodyFile ?? T("c.body.none")}"
                     + (draft.FaceFile is null ? string.Empty : $"   ·   {T("c.face")} {draft.FaceFile}");
        ModalPanel.Children.Add(PixelUi.Note(status, 16));

        if (draft.CropSource is { } source)
        {
            var editor = new CropEditor(source, draft.Crop, 460, 280) { Margin = new Thickness(0, 8, 0, 0) };
            var preview = new Image
            {
                Width = 128,
                Height = 128,
                Source = CharacterImages.Preview(source, draft.Crop),
                Stretch = System.Windows.Media.Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.NearestNeighbor);
            editor.CropChanged += (_, _) =>
            {
                draft.Crop = editor.Crop;
                preview.Source = CharacterImages.Preview(source, draft.Crop);
            };
            editor.GotKeyboardFocus += (_, _) => Focus("crop");

            var previewColumn = new StackPanel { Margin = new Thickness(24, 8, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            previewColumn.Children.Add(PixelUi.Note(T("c.preview"), 16));
            previewColumn.Children.Add(new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(CharacterImages.PortraitBackColor),
                Child = preview,
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(editor);
            row.Children.Add(previewColumn);
            ModalPanel.Children.Add(row);
            ModalPanel.Children.Add(PixelUi.Note(T("c.crop.hint"), 15));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(PixelUi.Action("cSave", T("p.save"), () => SaveCharacter(m, draft), 24, Focus));
        var cancel = PixelUi.Action("cCancel", T("p.cancel"), () =>
        {
            _draft = null;
            _focusKey = "charNew";
            BuildContent();
        }, 24, Focus);
        cancel.Margin = new Thickness(12, 0, 0, 0);
        buttons.Children.Add(cancel);
        ModalPanel.Children.Add(buttons);

        ContentPanel.IsEnabled = false;
        TabsPanel.IsEnabled = false;
        ModalLayer.Visibility = Visibility.Visible;
    }

    private static void ResetCrop(CharacterDraft draft)
    {
        if (draft.CropSource is { } source)
        {
            draft.Crop = SquareCrop.Suggested(source.PixelWidth, source.PixelHeight);
        }
    }

    private void PickImage(CharacterDraft draft, bool face)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Todos los archivos|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var image = CharacterImages.Load(dialog.FileName);
            var file = Path.GetFileName(dialog.FileName);
            if (face)
            {
                draft.Face = image;
                draft.FaceFile = file;
            }
            else
            {
                draft.Body = image;
                draft.BodyFile = file;
            }

            ResetCrop(draft);
            _message = null;
        }
        catch (IOException e)
        {
            _message = PlayersTexts.Format(Lang, "c.failed", e.Message);
        }

        _focusKey = face ? "cFace" : "cBody";
        BuildContent();
    }

    private void SaveCharacter(PlayersModel m, CharacterDraft draft)
    {
        if (draft.CropSource is not { } source)
        {
            _message = T("c.noimage");
            _focusKey = "cBody";
            BuildContent();
            return;
        }

        try
        {
            var body = draft.Body is null ? null : CharacterImages.EncodeFullBody(draft.Body);
            var portrait = CharacterImages.EncodePortrait(source, draft.Crop);
            m.UserCharacters.Add(body, portrait);
            m.Catalog.SetUserCharacters(m.UserCharacters.Load());
            _draft = null;
            _message = T("c.saved");
            _focusKey = "charNew";
            DataChanged?.Invoke();
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or InvalidOperationException)
        {
            _message = PlayersTexts.Format(Lang, "c.failed", e.Message);
        }

        BuildContent();
    }
}
