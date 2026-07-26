// ─────────────────────────────────────────────────────────────────────────────
// Global type aliases — resolves all WPF/WinForms/Drawing namespace collisions.
//
// WHY THIS FILE EXISTS:
//  * The csproj has <UseWindowsForms>true</UseWindowsForms> because
//    KeyPressService needs System.Windows.Forms.SendKeys.
//  * The csproj also references System.Drawing.Common because
//    ScreenCaptureService needs Bitmap/PNG encoding.
//  * Together those bring in three full namespaces that share dozens of
//    type names with WPF (Color, Brush, Rectangle, MessageBox, etc.).
//  * Without these aliases, every panel file gets CS0104 "ambiguous reference"
//    errors on every line that touches one of those types.
//
// THE RULE: WPF wins everywhere. The handful of files that genuinely need the
// Forms or Drawing version (KeyPressService for SendKeys, ScreenCaptureService
// for Bitmap) use fully qualified names or scoped using-aliases.
//
// NOTES:
//  * We do NOT alias Task — async return type aliases break the C# compiler.
//  * We do NOT alias Application — App.xaml.cs uses the fully qualified name.
//  * We do NOT alias Timer — the two files that use it use fully qualified names.
// ─────────────────────────────────────────────────────────────────────────────

// ── Controls / Shapes ────────────────────────────────────────────────────────
global using UserControl  = System.Windows.Controls.UserControl;
global using Button       = System.Windows.Controls.Button;
global using Rectangle    = System.Windows.Shapes.Rectangle;

// ── Input / Events ───────────────────────────────────────────────────────────
global using KeyEventArgs   = System.Windows.Input.KeyEventArgs;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using Cursors        = System.Windows.Input.Cursors;

// ── Layout / Geometry ────────────────────────────────────────────────────────
global using Point               = System.Windows.Point;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment   = System.Windows.VerticalAlignment;

// ── Media (colors, brushes) ──────────────────────────────────────────────────
global using Color           = System.Windows.Media.Color;
global using Brush           = System.Windows.Media.Brush;
global using Brushes         = System.Windows.Media.Brushes;
global using ColorConverter  = System.Windows.Media.ColorConverter;

// ── Top-level WPF types ──────────────────────────────────────────────────────
global using MessageBox = System.Windows.MessageBox;
global using Clipboard  = System.Windows.Clipboard;
