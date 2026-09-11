using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;

namespace WinCapture.Services;

public static class PdfExportService
{
    public static void Save(Bitmap bitmap, string path)
    {
        using var jpegStream = new MemoryStream();
        bitmap.Save(jpegStream, ImageFormat.Jpeg);
        var imageBytes = jpegStream.ToArray();
        var pageWidth = bitmap.Width * 72d / 96d;
        var pageHeight = bitmap.Height * 72d / 96d;
        var content = FormattableString.Invariant($"q {pageWidth:0.###} 0 0 {pageHeight:0.###} 0 0 cm /Image0 Do Q\n");
        var contentBytes = Encoding.ASCII.GetBytes(content);

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var offsets = new long[6];
        WriteAscii(output, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        offsets[1] = output.Position;
        WriteAscii(output, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = output.Position;
        WriteAscii(output, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = output.Position;
        WriteAscii(output, FormattableString.Invariant(
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth:0.###} {pageHeight:0.###}] /Resources << /XObject << /Image0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n"));

        offsets[4] = output.Position;
        WriteAscii(output, FormattableString.Invariant(
            $"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {bitmap.Width} /Height {bitmap.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {imageBytes.Length} >>\nstream\n"));
        output.Write(imageBytes);
        WriteAscii(output, "\nendstream\nendobj\n");

        offsets[5] = output.Position;
        WriteAscii(output, $"5 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        output.Write(contentBytes);
        WriteAscii(output, "endstream\nendobj\n");

        var crossReferenceOffset = output.Position;
        WriteAscii(output, "xref\n0 6\n0000000000 65535 f \n");
        for (var index = 1; index <= 5; index++)
        {
            WriteAscii(output, offsets[index].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        WriteAscii(output, $"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{crossReferenceOffset}\n%%EOF\n");
    }

    private static void WriteAscii(Stream stream, string value)
    {
        stream.Write(Encoding.Latin1.GetBytes(value));
    }
}
