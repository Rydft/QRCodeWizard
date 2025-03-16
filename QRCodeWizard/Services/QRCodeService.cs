using System.Drawing;
using QRCoder.Core;

namespace QRCodeWizard.Services;

public class QRCodeService : IDisposable
{
    private readonly QRCodeGenerator _qrGenerator = new();

    public IEnumerable<Bitmap> GenerateQRCode(IEnumerable<string> qrContents, Color color, Bitmap? logo = null)
    {
        foreach (var qrContent in qrContents)
        {
            using var qrData = _qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.H, forceUtf8:true);
            using var qr = new QRCode(qrData);
            
            Bitmap rawImage;
            if (logo != null)
            {
                rawImage = qr.GetGraphic(
                    pixelsPerModule: 10,
                    darkColor: color,
                    lightColor: Color.White,
                    icon: logo,
                    iconSizePercent: 23,
                    iconBorderWidth: 6,
                    drawQuietZones: true);
            }
            else
            {
                rawImage = qr.GetGraphic(
                    pixelsPerModule: 10, 
                    darkColor: color, 
                    lightColor: Color.White, 
                    drawQuietZones: true);
            }
            
            yield return new Bitmap(rawImage);
        }
    }

    public void Dispose()
    {
        _qrGenerator.Dispose();
    }
}