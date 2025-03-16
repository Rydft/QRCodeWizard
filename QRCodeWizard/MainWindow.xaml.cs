using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using QRCodeWizard.Services;
using System.Windows.Input;
using Application = System.Windows.Application;
using Image = System.Drawing.Image;

namespace QRCodeWizard;

public partial class MainWindow : IDisposable
{
    private readonly QRCodeService _qrCodeService;
    private readonly ConcurrentDictionary<string, Bitmap> _currentContents = new();
    private BlockingCollection<(string, Bitmap)> _generatedQRCodes = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private static Bitmap? _preloadedLogo;
    private readonly ThreadLocal<Bitmap?> _logo = new(GetSafeLogo);
    
    private static readonly Regex EmailRegex = EmailFormatRegex();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PreJIT()
    {
        _ = _qrCodeService.GenerateQRCode(["preload"], System.Drawing.Color.Black, _logo.Value);
    }

    public MainWindow()
    {
        InitializeComponent();
        _qrCodeService = new QRCodeService();
        PreJIT();
        ThreadPool.SetMinThreads(Environment.ProcessorCount * 3, Environment.ProcessorCount * 3);
        _ = _qrCodeService;
        _ = _logo.Value;
        LoadLogo();
        Closing += MainWindow_Closing;
        
        KeyDown += (s, e) => 
        {
            if (e.Key == Key.Escape)
                Close();
        };
        
        EmailTextBox.AddHandler(System.Windows.DataObject.PastingEvent, new DataObjectPastingEventHandler(OnPaste));
        
        CancelButton.Visibility = Visibility.Collapsed;
    }
    private static Bitmap? GetSafeLogo()
    {
        _preloadedLogo ??= LoadLogo();
        return _preloadedLogo?.Clone() as Bitmap;
    }
    private static Bitmap? LoadLogo()
    {
        try
        {
            var logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "logo_ev.bmp");
            if (File.Exists(logoPath))
            {
                return new Bitmap(logoPath);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erreur lors du chargement du logo: {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        return null;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        Dispose();
    }
    
    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = false;
        
        if (EmailTextBox != null)
        {
            EmailTextBox.IsEnabled = false;
        }
        
        try
        {
            var inputText = EmailTextBox?.Text.Trim() ?? string.Empty;
            
            if (string.IsNullOrWhiteSpace(inputText))
            {
                System.Windows.MessageBox.Show("Veuillez saisir au moins une adresse e-mail ou URL.", "Saisie requise", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var contents = inputText.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(content => content.Trim())
                .Where(content => !string.IsNullOrWhiteSpace(content))
                .Distinct()
                .ToArray();

            if (contents.Length == 0)
            {
                System.Windows.MessageBox.Show("Veuillez saisir au moins une adresse e-mail ou URL valide.", "Saisie requise", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var validContents = contents
                .Where(content => IsValidEmail(content) || IsValidUrl(content))
                .ToArray();
                
            if (validContents.Length == 0)
            {
                System.Windows.MessageBox.Show("Aucune entrée valide trouvée. Veuillez saisir au moins une adresse e-mail ou URL valide.", "Entrées invalides", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            _cancellationTokenSource = new CancellationTokenSource();
            
            await GenerateQRCodesAsync(validContents, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Génération annulée";
            
            QRCodeGrid.Children.Clear();
            foreach (var qrCode in _currentContents.Values)
            {
                qrCode.Dispose();
            }
            _currentContents.Clear();
            
            if (QRCodeGrid.Children.Count == 0)
            {
                QRCodeViewbox.Visibility = Visibility.Collapsed;
                PlaceholderText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erreur lors de la génération des QR codes: {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GenerateButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = !_currentContents.IsEmpty;
            
            if (EmailTextBox != null)
            {
                EmailTextBox.IsEnabled = true;
            }
            
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }
    
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
    }

    private async Task GenerateQRCodesAsync(string[] contents, CancellationToken cancellationToken)
    {
        QRCodeGrid.Children.Clear();
        foreach (var qrCode in _currentContents.Values)
        {
            qrCode.Dispose();
        }
        _currentContents.Clear();
        _generatedQRCodes = new BlockingCollection<(string, Bitmap)>();

        var count = contents.Length;
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        QRCodeGrid.Columns = columns;

        QRCodeViewbox.Visibility = Visibility.Visible;
        PlaceholderText.Visibility = Visibility.Collapsed;

        cancellationToken.ThrowIfCancellationRequested();
        var generationTask = Parallel.ForEachAsync(contents, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        }, (content, token) =>
        {
            var qrCode = _qrCodeService.GenerateQRCode(
                [content],
                System.Drawing.Color.FromArgb(255, 0, 67, 84),
                _logo.Value).FirstOrDefault();

            token.ThrowIfCancellationRequested();
            _generatedQRCodes.Add((content, qrCode!), token);
            return ValueTask.CompletedTask;
        });

        var completeTask = generationTask.ContinueWith(t =>
            _generatedQRCodes.CompleteAdding(), cancellationToken);

        var processingTask = Task.Run(async () =>
        {
            foreach (var (content, qrCode) in _generatedQRCodes.GetConsumingEnumerable(cancellationToken))
            {
                _currentContents.TryAdd(content, qrCode);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var image = new System.Windows.Controls.Image
                    {
                        Margin = new Thickness(10),
                        Stretch = Stretch.Uniform,
                        Source = ConvertBitmapToBitmapImage(qrCode!)
                    };
                    AddQRCodeWithAnimation(image, content, _currentContents.Count - 1);
                    var plural = _currentContents.Count > 1 ? "s" : string.Empty;
                    StatusText.Text = $"{_currentContents.Count}{(contents.Length > 1 ? $"/{contents.Length}" : string.Empty)} QR code{plural} généré{plural}...";
                });
            }
        }, cancellationToken);

        await Task.WhenAll(generationTask, completeTask, processingTask);

        var finalPlural = _currentContents.Count != 1 ? "s" : "";
        StatusText.Text = $"{_currentContents.Count} QR code{finalPlural} généré{finalPlural}";
        SaveButton.IsEnabled = !_currentContents.IsEmpty;
    }


    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;
            
        if (email.Length > 254)
            return false;

        return email.Contains('@') && EmailRegex.IsMatch(email);
    }
    
    private static bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
            
        if (!url.StartsWith("http://") && !url.StartsWith("https://") && !url.StartsWith("ftp://"))
            return false;
            
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult);
    }

    private void AddQRCodeWithAnimation(System.Windows.Controls.Image image, string content, int index)
    {
        image.RenderTransform = new ScaleTransform(0, 0);
        image.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        
        var contextMenu = new ContextMenu();
        var copyMenuItem = new MenuItem { Header = "Copier le QR code" };
        copyMenuItem.Click += (s, e) => CopyImageToClipboard(image);
        
        var copyContentMenuItem = new MenuItem { Header = "Copier le contenu" };
        copyContentMenuItem.Click += (s, e) => CopyContentToClipboard(content);
        
        var saveMenuItem = new MenuItem { Header = "Enregistrer le QR code" };
        saveMenuItem.Click += (s, e) => SaveQRCode(content);
        
        var openMenuItem = new MenuItem { Header = "Ouvrir dans l'Explorateur" };
        openMenuItem.Click += (s, e) => OpenQRCodeInExplorer(content);
        
        contextMenu.Items.Add(copyMenuItem);
        contextMenu.Items.Add(copyContentMenuItem);
        contextMenu.Items.Add(saveMenuItem);
        contextMenu.Items.Add(openMenuItem);
        
        image.ContextMenu = contextMenu;
        
        image.ToolTip = content;
        
        image.Tag = content;
        
        image.MouseLeftButtonDown += QRCode_MouseLeftButtonDown;
        image.MouseEnter += QRCode_MouseEnter;
        image.MouseLeave += QRCode_MouseLeave;
        
        QRCodeGrid.Children.Add(image);
        
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        
        image.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        image.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
    
    private void QRCode_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image { Tag: string content })
            return;
        if (_currentContents.ContainsKey(content))
        {
            HoverText.Text = content;
        }
    }
    
    private void QRCode_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HoverText.Text = string.Empty;
    }
    
    private void QRCode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not System.Windows.Controls.Image { Tag: string content })
            return;
        if (_currentContents.ContainsKey(content))
        {
            OpenQRCodeInExplorer(content);
        }
            
        e.Handled = true;
    }
    
    private void OpenQRCodeInExplorer(string content)
    {
        if (!_currentContents.TryGetValue(content, out var currentContent))
        {
            System.Windows.MessageBox.Show("QR code non trouvé.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        try
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), "QRCodeWizard");
            Directory.CreateDirectory(tempFolder);
            
            var fileName = GetSafeFileName(content);
            var filePath = Path.Combine(tempFolder, $"{fileName}.png");
            
            currentContent.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
            
            StatusText.Text = $"QR code ouvert pour {content}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erreur lors de l'ouverture du QR code: {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyImageToClipboard(System.Windows.Controls.Image image)
    {
        if (image.Source is not BitmapSource bitmapSource)
            return;
        System.Windows.Clipboard.SetImage(bitmapSource);
        StatusText.Text = "QR code copié dans le presse-papiers";
    }

    private void CopyContentToClipboard(string content)
    {
        System.Windows.Clipboard.SetText(content);
        StatusText.Text = "Contenu copié dans le presse-papiers";
    }

    private static BitmapImage ConvertBitmapToBitmapImage(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
        memory.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.StreamSource = memory;
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.EndInit();
        bitmapImage.Freeze();

        return bitmapImage;
    }

    private void EmailTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GenerateButton_Click(sender, e);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EmailTextBox.Focus();
    }
    
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentContents.IsEmpty)
        {
            System.Windows.MessageBox.Show("Aucun QR code à enregistrer. Veuillez d'abord générer des QR codes.", "Aucun contenu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        
        SaveQRCodes();
    }
    
    private void SaveQRCodes()
    {
        var folderDialog = new FolderBrowserDialog
        {
            Description = "Sélectionnez un dossier pour enregistrer les QR codes",
            UseDescriptionForTitle = true
        };
        
        var result = folderDialog.ShowDialog();

        if (result != System.Windows.Forms.DialogResult.OK)
            return;
        var folderPath = folderDialog.SelectedPath;
        var savedCount = 0;
            
        StatusText.Text = "Enregistrement des QR codes...";

        foreach (var (content, qrCode) in _currentContents)
        {
            try
            {
                var fileName = GetSafeFileName(content);
                var filePath = Path.Combine(folderPath, $"{fileName}.png");
                    
                var counter = 1;
                while (File.Exists(filePath))
                {
                    filePath = Path.Combine(folderPath, $"{fileName}_{counter}.png");
                    counter++;
                }
                    
                qrCode.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                savedCount++;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Erreur lors de l'enregistrement du QR code pour '{content}': {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
            
        StatusText.Text = $"Enregistré {savedCount} QR code{(savedCount != 1 ? "s" : "")} dans {folderPath}";
            
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", folderPath);
        }
        catch
        {
            // Ignore
        }
    }
    
    private void SaveQRCode(string content)
    {
        if (_currentContents.ContainsKey(content))
        {
            System.Windows.MessageBox.Show("QR code non trouvé.", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = GetSafeFileName(content),
            DefaultExt = ".png",
            Filter = "Images PNG|*.png"
        };

        if (saveDialog.ShowDialog() != true)
            return;
        try
        {
            _currentContents[content].Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            StatusText.Text = $"QR code enregistré dans {saveDialog.FileName}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erreur lors de l'enregistrement du QR code: {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    private static string GetSafeFileName(string input)
    {
        var invalidChars = new string(Path.GetInvalidFileNameChars());
        var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", Regex.Escape(invalidChars));
        
        var safeName = Regex.Replace(input, invalidRegStr, "_");
        
        if (safeName.Length > 100)
        {
            safeName = safeName[..100];
        }
        
        return safeName;
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
            return;
        var text = (string)e.DataObject.GetData(System.Windows.DataFormats.Text);
        if (string.IsNullOrEmpty(text))
            return;
        text = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        switch (sender)
        {
            case System.Windows.Controls.TextBox textBox:
            {
                var caretIndex = textBox.CaretIndex;
                textBox.Text = textBox.Text.Insert(caretIndex, text);
                textBox.CaretIndex = caretIndex + text.Length;
                break;
            }
            case System.Windows.Controls.RichTextBox richTextBox:
                richTextBox.Selection.Text = text;
                break;
        }
    }

    public void Dispose()
    {
        _qrCodeService.Dispose();
        _logo.Dispose();
        
        foreach (var qrCode in _currentContents.Values)
        {
            qrCode.Dispose();
        }
        
        _currentContents.Clear();
    }

    [GeneratedRegex(@"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "fr-FR")]
    private static partial Regex EmailFormatRegex();
}