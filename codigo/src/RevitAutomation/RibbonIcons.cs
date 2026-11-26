using Media = System.Windows.Media;

namespace RevitAutomation.Addin;

internal enum RibbonIcon
{
    Pdf,
    Cad,
    Tag,
    Pipe,
    Duct,
    CableTray,
    Extinguisher,
    Luminaire,
    Sheets,
    GenericModel,
    Region,
    Shaft,
    Insulation,
    TechRoom,
    TechRoomWaterMeters,
    TechRoomElectrical,
    TechRoomFire,
    TechRoomPlumbing
}

/// <summary>Iconos vectoriales de la cinta, dibujados en código (sin archivos de imagen).</summary>
internal static class RibbonIcons
{

    internal static Media.ImageSource Create(RibbonIcon icon, int size)
    {
        if (size != 16 && size != 32)
            throw new ArgumentOutOfRangeException(nameof(size));

        var drawing = new Media.DrawingGroup();
        using (Media.DrawingContext context = drawing.Open())
        {
            // Fix the canvas bounds, including transparent margins, for both sizes.
            context.DrawRectangle(Media.Brushes.Transparent, null, new System.Windows.Rect(0, 0, 32, 32));

            void Fill(string path, Media.Brush brush) =>
                context.DrawGeometry(brush, null, Media.Geometry.Parse(path));

            void Stroke(string path, Media.Brush brush, double width = 2)
            {
                var pen = new Media.Pen(brush, width)
                {
                    StartLineCap = Media.PenLineCap.Round,
                    EndLineCap = Media.PenLineCap.Round,
                    LineJoin = Media.PenLineJoin.Round
                };
                context.DrawGeometry(null, pen, Media.Geometry.Parse(path));
            }

            Media.Brush blue = new Media.SolidColorBrush(Media.Color.FromRgb(36, 112, 184));
            Media.Brush red = new Media.SolidColorBrush(Media.Color.FromRgb(205, 57, 64));
            Media.Brush green = new Media.SolidColorBrush(Media.Color.FromRgb(38, 139, 94));
            Media.Brush orange = new Media.SolidColorBrush(Media.Color.FromRgb(220, 123, 36));
            Media.Brush paper = new Media.SolidColorBrush(Media.Color.FromRgb(245, 248, 252));

            switch (icon)
            {
                case RibbonIcon.Pdf:
                    Fill("M 7,3 L 20,3 26,9 26,29 7,29 Z", paper);
                    Stroke("M 7,3 L 20,3 26,9 26,29 7,29 Z M 20,3 L 20,9 26,9", red);
                    Fill("M 4,14 L 21,14 21,23 4,23 Z", red);
                    // Geometric PDF lettering avoids any dependency on installed fonts.
                    Stroke("M 7,21 L 7,16 10,16 10,18.5 7,18.5 M 12,21 L 12,16 14,16 15,17 15,20 14,21 Z M 18,21 L 18,16 20,16 M 18,18.5 L 20,18.5", Media.Brushes.White, 1);
                    Stroke("M 11,26 L 21,26", red, 1.5);
                    break;

                case RibbonIcon.Cad:
                    Fill("M 3,5 L 29,5 29,27 3,27 Z", paper);
                    Stroke("M 3,5 L 29,5 29,27 3,27 Z", blue);
                    Stroke("M 8,22 L 8,10 23,10 23,22 18,22 18,16 8,16 M 14,10 L 14,16", blue);
                    Stroke("M 5,3 L 27,3 M 5,2 L 5,4 M 27,2 L 27,4 M 5,29 L 27,29 M 5,28 L 5,30 M 27,28 L 27,30", blue, 1);
                    break;

                case RibbonIcon.Tag:
                {
                    // Label tag with its eyelet and string, and text lines.
                    const string tag = "M 3,9 L 19,9 29,17 19,25 3,25 Z";
                    Fill(tag, new Media.SolidColorBrush(Media.Color.FromRgb(255, 214, 102)));
                    Stroke(tag, orange, 1.6);
                    context.DrawEllipse(Media.Brushes.White, new Media.Pen(orange, 1.2), new System.Windows.Point(21.5, 17), 2, 2);
                    Stroke("M 6.5,14 L 15,14 M 6.5,17.5 L 13,17.5 M 6.5,21 L 15,21", blue, 1.6);
                    Stroke("M 22.5,15.5 C 25,10 27,6 30,4", Media.Brushes.Gray, 1);
                    break;
                }

                case RibbonIcon.Pipe:
                {
                    // Pipe with a 90° elbow and flanges.
                    Media.Brush pipe = new Media.SolidColorBrush(Media.Color.FromRgb(36, 112, 184));
                    Media.Brush shine = new Media.SolidColorBrush(Media.Color.FromRgb(140, 190, 235));
                    // Thick stroke kept 4 units inside the 32x32 canvas (the icon size must stay exact).
                    Stroke("M 4.5,23 L 14,23 C 20,23 23,20 23,14 L 23,4.5", pipe, 8);
                    Stroke("M 4.5,20.5 L 14,20.5 C 18.5,20.5 20.5,18.5 20.5,14 L 20.5,4.5", shine, 1.6);
                    Fill("M 8,17.5 L 11,17.5 11,28.5 8,28.5 Z M 17.5,7 L 28.5,7 28.5,10 17.5,10 Z", new Media.SolidColorBrush(Media.Color.FromRgb(20, 70, 125)));
                    break;
                }

                case RibbonIcon.Duct:
                {
                    // Rectangular duct in perspective with a joint.
                    Media.Brush front = new Media.SolidColorBrush(Media.Color.FromRgb(150, 165, 180));
                    Media.Brush top = new Media.SolidColorBrush(Media.Color.FromRgb(205, 215, 225));
                    Media.Brush side = new Media.SolidColorBrush(Media.Color.FromRgb(105, 120, 138));
                    Media.Brush edge = new Media.SolidColorBrush(Media.Color.FromRgb(55, 66, 80));
                    Fill("M 2,13 L 21,13 21,27 2,27 Z", front);
                    Fill("M 2,13 L 10,6 29,6 21,13 Z", top);
                    Fill("M 21,13 L 29,6 29,20 21,27 Z", side);
                    Stroke("M 2,13 L 21,13 21,27 2,27 Z M 2,13 L 10,6 29,6 21,13 M 21,27 L 29,20 29,6", edge, 1.1);
                    Stroke("M 11,13 L 11,27 M 11,13 L 19,6", edge, 1.6);
                    break;
                }

                case RibbonIcon.CableTray:
                {
                    // Ladder cable tray in perspective with coloured cables.
                    Media.Brush rail = new Media.SolidColorBrush(Media.Color.FromRgb(110, 118, 128));
                    Stroke("M 6,15 C 12,14 20,13 30,12", new Media.SolidColorBrush(Media.Color.FromRgb(220, 123, 36)), 2);
                    Stroke("M 4,18 C 12,17 20,16 28,15", new Media.SolidColorBrush(Media.Color.FromRgb(36, 112, 184)), 2);
                    Stroke("M 3,21 C 10,20 18,19 26,18", green, 2);
                    Stroke("M 2,25 L 24,25 M 8,17 L 30,17", rail, 2.4);
                    Stroke("M 2,25 L 2,21 M 24,25 L 24,21 M 8,17 L 8,13 M 30,17 L 30,13", rail, 1.6);
                    Stroke("M 6,25 L 12,17 M 13,25 L 19,17 M 20,25 L 26,17", rail, 1.4);
                    break;
                }

                case RibbonIcon.Extinguisher:
                {
                    // Fire extinguisher: red cylinder, label, valve, handle and hose.
                    Media.Brush dark = new Media.SolidColorBrush(Media.Color.FromRgb(45, 45, 52));
                    Fill("M 12,10 L 21,10 Q 23.5,10 23.5,12.5 L 23.5,28 Q 23.5,30.5 21,30.5 L 12,30.5 Q 9.5,30.5 9.5,28 L 9.5,12.5 Q 9.5,10 12,10 Z",
                        new Media.SolidColorBrush(Media.Color.FromRgb(210, 32, 38)));
                    Fill("M 9.5,17 L 23.5,17 23.5,23 9.5,23 Z", Media.Brushes.White);
                    Stroke("M 12.5,20 L 20.5,20", red, 1.4);
                    Fill("M 14,5.5 L 19,5.5 19,10 14,10 Z", dark);
                    Stroke("M 18,6 L 26,3.5 M 15,5.5 L 22,2", dark, 1.6);
                    Stroke("M 14,7.5 C 8,7.5 5,11 5,17 L 5,22", dark, 1.8);
                    Fill("M 3.5,21.5 L 6.5,21.5 6,25 4,25 Z", dark);
                    break;
                }

                case RibbonIcon.Luminaire:
                {
                    // Pendant luminaire: cord, shade, lit bulb and light rays.
                    Media.Brush shade = new Media.SolidColorBrush(Media.Color.FromRgb(58, 68, 84));
                    Media.Brush light = new Media.SolidColorBrush(Media.Color.FromRgb(255, 196, 40));
                    Stroke("M 16,2 L 16,8", shade, 1.6);
                    Fill("M 10,8 L 22,8 28,17 4,17 Z", shade);
                    context.DrawEllipse(light, null, new System.Windows.Point(16, 18.5), 4.5, 3.2);
                    Stroke("M 16,24.5 L 16,29.5 M 9.5,22.5 L 6.5,26.5 M 22.5,22.5 L 25.5,26.5 M 5,20 L 2.5,21.5 M 27,20 L 29.5,21.5", light, 1.8);
                    break;
                }

                case RibbonIcon.Sheets:
                {
                    // Drawing sheet: paper, a plan on it and the title block at the bottom right.
                    Media.Brush sheetEdge = new Media.SolidColorBrush(Media.Color.FromRgb(52, 62, 78));
                    Media.Brush plan = new Media.SolidColorBrush(Media.Color.FromRgb(36, 112, 184));
                    Media.Brush back = new Media.SolidColorBrush(Media.Color.FromRgb(200, 210, 222));
                    Fill("M 7,3 L 30,3 30,24 7,24 Z", back);
                    Fill("M 2,7 L 26,7 26,29 2,29 Z", paper);
                    Stroke("M 2,7 L 26,7 26,29 2,29 Z", sheetEdge, 1.4);
                    Stroke("M 5,10 L 17,10 17,20 5,20 Z M 11,10 L 11,16 M 5,16 L 14,16", plan, 1.3);
                    Fill("M 18,22 L 26,22 26,29 18,29 Z", orange);
                    Stroke("M 18,22 L 26,22 M 18,22 L 18,29", sheetEdge, 1);
                    break;
                }

                case RibbonIcon.GenericModel:
                {
                    // Generic model: shaded cube.
                    Media.Brush front = new Media.SolidColorBrush(Media.Color.FromRgb(150, 110, 200));
                    Media.Brush top = new Media.SolidColorBrush(Media.Color.FromRgb(200, 175, 235));
                    Media.Brush side = new Media.SolidColorBrush(Media.Color.FromRgb(105, 75, 150));
                    Media.Brush edge = new Media.SolidColorBrush(Media.Color.FromRgb(60, 40, 90));
                    Fill("M 4,12 L 20,12 20,28 4,28 Z", front);
                    Fill("M 4,12 L 12,4 28,4 20,12 Z", top);
                    Fill("M 20,12 L 28,4 28,20 20,28 Z", side);
                    Stroke("M 4,12 L 20,12 20,28 4,28 Z M 4,12 L 12,4 28,4 20,12 M 20,28 L 28,20 28,4", edge, 1.2);
                    break;
                }

                case RibbonIcon.Region:
                {
                    // Provisional icon for REGION: the text «REG» on a coloured square (final artwork pending).
                    Media.Brush regionBack = new Media.SolidColorBrush(Media.Color.FromRgb(38, 139, 94));
                    context.DrawRoundedRectangle(regionBack, null, new System.Windows.Rect(1, 1, 30, 30), 5, 5);
                    var text = new Media.FormattedText("REG", System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new Media.Typeface(new Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                            System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                        12, Media.Brushes.White, 1.0);
                    context.DrawText(text, new System.Windows.Point((32 - text.Width) / 2, (32 - text.Height) / 2));
                    break;
                }

                case RibbonIcon.Shaft:
                {
                    // Provisional icon for «Patinillo de fontanería»: the text «PAT» on a blue square (final artwork pending).
                    Media.Brush shaftBack = new Media.SolidColorBrush(Media.Color.FromRgb(31, 102, 178));
                    context.DrawRoundedRectangle(shaftBack, null, new System.Windows.Rect(1, 1, 30, 30), 5, 5);
                    var text = new Media.FormattedText("PAT", System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new Media.Typeface(new Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                            System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                        12, Media.Brushes.White, 1.0);
                    context.DrawText(text, new System.Windows.Point((32 - text.Width) / 2, (32 - text.Height) / 2));
                    break;
                }

                case RibbonIcon.TechRoom:
                case RibbonIcon.TechRoomWaterMeters:
                case RibbonIcon.TechRoomElectrical:
                case RibbonIcon.TechRoomFire:
                case RibbonIcon.TechRoomPlumbing:
                {
                    // Provisional icons for «Salas técnicas»: short text on a coloured square (final artwork pending).
                    var (roomText, roomColor) = icon switch
                    {
                        RibbonIcon.TechRoomWaterMeters => ("CA", Media.Color.FromRgb(31, 102, 178)),
                        RibbonIcon.TechRoomElectrical => ("ELE", Media.Color.FromRgb(214, 160, 20)),
                        RibbonIcon.TechRoomFire => ("PCI", Media.Color.FromRgb(205, 57, 64)),
                        RibbonIcon.TechRoomPlumbing => ("FON", Media.Color.FromRgb(38, 139, 94)),
                        _ => ("ST", Media.Color.FromRgb(70, 84, 104))
                    };
                    context.DrawRoundedRectangle(new Media.SolidColorBrush(roomColor), null, new System.Windows.Rect(1, 1, 30, 30), 5, 5);
                    var text = new Media.FormattedText(roomText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new Media.Typeface(new Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                            System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                        roomText.Length > 2 ? 12 : 14, Media.Brushes.White, 1.0);
                    context.DrawText(text, new System.Windows.Point((32 - text.Width) / 2, (32 - text.Height) / 2));
                    break;
                }

                case RibbonIcon.Insulation:
                {
                    // Provisional icon for «Aislamiento RITE»: the text «AIS» on an orange square (final artwork pending).
                    Media.Brush insulationBack = new Media.SolidColorBrush(Media.Color.FromRgb(214, 110, 32));
                    context.DrawRoundedRectangle(insulationBack, null, new System.Windows.Rect(1, 1, 30, 30), 5, 5);
                    var text = new Media.FormattedText("AIS", System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new Media.Typeface(new Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                            System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                        12, Media.Brushes.White, 1.0);
                    context.DrawText(text, new System.Windows.Point((32 - text.Width) / 2, (32 - text.Height) / 2));
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(icon));
            }
        }

        drawing.Transform = new Media.ScaleTransform(size / 32.0, size / 32.0);
        var image = new Media.DrawingImage(drawing);
        // Immutable WPF resources can safely be retained by Revit's ribbon.
        image.Freeze();
        return image;
    }
}
