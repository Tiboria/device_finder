using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeviceFinder.App.Services;

namespace DeviceFinder.App;

/// <summary>
/// Fenêtre d'aide dédiée à la durée de bail DHCP, ouverte par le bouton « ⓘ »
/// à côté de la liste de durée. Le concept de persistance du bail est un point
/// de sécurité (l'équipement garde son IP après coupure du serveur) : il mérite
/// un panneau lisible — titre, puces, texte à taille normale — pas une infobulle
/// éphémère. Reconstruite à chaque ouverture : les textes sont traduits via
/// <see cref="Loc"/> dans la langue active. Fermeture par Échap, la croix, ou le
/// bouton.
/// </summary>
public sealed class LeaseHelpWindow : Window
{
    public LeaseHelpWindow()
    {
        Title = Loc.Get("LeaseHelp.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MaxWidth = 580;

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = Loc.Get("LeaseHelp.Heading"),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        AddBullet(root, Loc.Get("LeaseHelp.Bullet1"));
        AddBullet(root, Loc.Get("LeaseHelp.Bullet2"));
        AddBullet(root, Loc.Get("LeaseHelp.Bullet3"));

        root.Children.Add(new TextBlock
        {
            Text = Loc.Get("LeaseHelp.Footer"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var closeButton = new Button
        {
            Content = Loc.Get("LeaseHelp.Close"),
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => Close();
        root.Children.Add(closeButton);

        Content = root;

        // Échap ferme la fenêtre, mécanisme principal avec la croix.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>Une puce : tiret, texte lisible (13 px), retour à la ligne automatique.</summary>
    private static void AddBullet(StackPanel parent, string text)
    {
        var bullet = new TextBlock
        {
            Text = "•  " + text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        parent.Children.Add(bullet);
    }
}
