using HarborBackup.Desktop.Styling;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class InputFrameTests
{
    [TestMethod]
    public async Task NativeTextEditorFitsViewportAfterInheritedFontChanges()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form();
                var editor = new TextBox { Width = 420, PlaceholderText = "筛选项目、路径、动作或错误…" };
                var frame = new InputFrame(editor);
                form.ClientSize = new System.Drawing.Size(480, 100);
                frame.Location = new System.Drawing.Point(12, 12);
                form.Controls.Add(frame);
                form.Show();
                foreach (var points in new[] { 10f, 14f, 18f, 10f })
                {
                    using var font = new System.Drawing.Font("Microsoft YaHei UI", points);
                    form.Font = font;
                    form.PerformLayout();
                    Application.DoEvents();
                    var placeholderHeight = TextRenderer.MeasureText(editor.PlaceholderText, editor.Font, System.Drawing.Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height + 1;
                    Assert.IsGreaterThanOrEqualTo(placeholderHeight, editor.ClientSize.Height, "Placeholder drawing includes a one-pixel top offset, even without a native border.");
                    Assert.IsGreaterThanOrEqualTo(editor.PreferredHeight, editor.Parent!.ClientSize.Height,
                        $"Viewport clips native editor at {points} pt: editor={editor.Height}, preferred={editor.PreferredHeight}, viewport={editor.Parent.Height}");
                    Assert.IsLessThanOrEqualTo(editor.Parent.ClientSize.Height, editor.Bottom);
                    var screenshots = Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR");
                    if (screenshots is not null)
                    {
                        // WM_PRINT omits native placeholders. Render the same glyphs as real
                        // input as well, so bitmap review can inspect the full character height.
                        Directory.CreateDirectory(screenshots);
                        editor.Text = "筛选项目、路径、动作或错误 / Filter Agyp";
                        using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(screenshots, $"filter-{points}pt.png"));
                        editor.Clear();
                    }
                }
                form.Close();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
