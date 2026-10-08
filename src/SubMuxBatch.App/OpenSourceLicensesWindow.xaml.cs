using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SubMuxBatch.App.Localization;
using SubMuxBatch.App.Services;
using SubMuxBatch.Core.Configuration;

namespace SubMuxBatch.App;

public partial class OpenSourceLicensesWindow : Window
{
    private const string CorrespondingSourceUrl =
        "https://github.com/KimPig/SubMuxBatch/tree/main/third-party-sources";

    private const string MitTerms = """
        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    private const string MediaInfoLicense = """
        BSD 2-Clause License

        Copyright (c) 2002-2020, MediaArea.net SARL
        All rights reserved.

        Redistribution and use in source and binary forms, with or without
        modification, are permitted provided that the following conditions are met:

        * Redistributions of source code must retain the above copyright notice, this
          list of conditions and the following disclaimer.

        * Redistributions in binary form must reproduce the above copyright notice,
          this list of conditions and the following disclaimer in the documentation
          and/or other materials provided with the distribution.

        THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
        AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
        IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
        DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
        FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
        DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
        SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
        CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
        OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
        OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
        """;

    private readonly IReadOnlyList<LicenseEntry> _entries;

    public OpenSourceLicensesWindow()
    {
        InitializeComponent();
        _entries = CreateEntries();
        ComponentList.ItemsSource = _entries;
        ComponentList.SelectedIndex = 0;
        Loaded += (_, _) => WindowPlacementHelper.FitToCurrentWorkingArea(this);
    }

    private static IReadOnlyList<LicenseEntry> CreateEntries() =>
    [
        new(
            "FFmpeg 8.1 GPL build — GPL-3.0-or-later",
            WithSourceNotice(ReadResource(
                typeof(OpenSourceLicensesWindow).Assembly,
                "SubMuxBatch.App.Resources.FFmpeg-GPL-3.0.txt")),
            "https://ffmpeg.org/"),
        new(
            "x265 — GPL-2.0-or-later",
            WithSourceNotice(ReadResource(
                typeof(OpenSourceLicensesWindow).Assembly,
                "SubMuxBatch.App.Resources.x265-GPL-2.0-or-later.txt")),
            "https://github.com/videolan/x265"),
        new(
            "Opus — BSD-3-Clause",
            WithSourceNotice(ReadResource(
                typeof(OpenSourceLicensesWindow).Assembly,
                "SubMuxBatch.App.Resources.Opus-BSD-3-Clause.txt")),
            "https://opus-codec.org/"),
        new(
            "MKVToolNix 102.0 — GPL-2.0-only",
            WithSourceNotice(ReadResource(
                typeof(AppSettings).Assembly,
                "SubMuxBatch.Core.Resources.mkvtoolnix.COPYING.txt")),
            "https://mkvtoolnix.download/"),
        new(
            "MKVToolNix 102.0 — bundled dependency notices",
            ReadMkvToolNixNotices(),
            "https://mkvtoolnix.download/doc/README.txt"),
        new(
            "LAPSE 2.2.4 — GPL-3.0-or-later",
            WithSourceNotice(ReadResource(
                typeof(AppSettings).Assembly,
                "SubMuxBatch.Core.Resources.lapse.LICENSE.txt")),
            "https://github.com/Schwponaco-org/lapse/releases/tag/v2.2.4"),
        new(
            "LAPSE: FFmpeg 7.1 — LGPL-2.1-or-later",
            ReadLapseNotice("FFmpeg-LGPL-2.1-or-later.txt"),
            "https://ffmpeg.org/releases/ffmpeg-7.1.html"),
        new(
            "LAPSE: FFTW 3.3.10 — GPL-2.0-or-later",
            WithSourceNotice(ReadLapseNotice("FFTW-GPL-2.0-or-later.txt")),
            "https://www.fftw.org/"),
        new(
            "LAPSE: libfvad — BSD-3-Clause",
            ReadLapseNotice("libfvad-BSD-3-Clause.txt"),
            "https://github.com/dpirch/libfvad/tree/532ab666c20d3cfda38bca63abbb0f152706c369"),
        new(
            "LAPSE: zlib 1.3.2 — zlib License",
            ReadLapseNotice("zlib-License.txt"),
            "https://github.com/madler/zlib/releases/tag/v1.3.2"),
        new(
            "LAPSE: ONNX Runtime 1.28.0 — MIT",
            ReadLapseNotice("ONNX-Runtime-MIT.txt"),
            "https://github.com/microsoft/onnxruntime/releases/tag/v1.28.0"),
        new(
            "LAPSE: ONNX Runtime — Third-party notices",
            ReadLapseNotice("ONNX-Runtime-ThirdPartyNotices.txt"),
            "https://github.com/microsoft/onnxruntime/blob/v1.28.0/ThirdPartyNotices.txt"),
        new(
            "LAPSE: Silero VAD 6.2.1 — MIT",
            ReadLapseNotice("Silero-VAD-MIT.txt"),
            "https://github.com/snakers4/silero-vad/releases/tag/v6.2.1"),
        new(
            "SubMux Sans — SIL Open Font License 1.1",
            ReadResource(
                typeof(AppSettings).Assembly,
                "SubMuxBatch.Core.Resources.SubMuxSans-OFL.txt"),
            "https://github.com/KimPig/SubMuxSans"),
        new(
            "MediaInfoLib 26.1.0 — BSD-2-Clause",
            MediaInfoLicense,
            "https://mediaarea.net/MediaInfo"),
        new(
            "libse 5.1.0 — MIT",
            "MIT License\n\nCopyright (c) 2026 Nikolaj Olsson\n\n" + MitTerms,
            "https://github.com/SubtitleEdit/subtitleedit/tree/main/src/libse"),
        new(
            "SkiaSharp 3.119.4 — MIT",
            """
            MIT License

            Copyright (c) 2015-2016 Xamarin, Inc.
            Copyright (c) 2017-2018 Microsoft Corporation.

            """ + MitTerms,
            "https://github.com/mono/SkiaSharp"),
        new(
            "UTF.Unknown 2.6.0 — MPL-1.1",
            """
            UTF.Unknown is distributed under the Mozilla Public License 1.1.

            The license text and corresponding source are available from the project page:
            https://github.com/CharsetDetector/UTF-unknown/blob/master/license/MPL-1.1.txt
            """,
            "https://github.com/CharsetDetector/UTF-unknown")
    ];

    private void ComponentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ComponentList.SelectedItem as LicenseEntry;
        LicenseTextBox.Text = selected?.Text ?? string.Empty;
        LicenseTextBox.ScrollToHome();
        ProjectPageButton.IsEnabled = selected is not null;
    }

    private void ProjectPage_Click(object sender, RoutedEventArgs e)
    {
        if (ComponentList.SelectedItem is not LicenseEntry selected)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(selected.ProjectUrl)
        {
            UseShellExecute = true
        });
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing license resource: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadLapseNotice(string fileName) =>
        ReadResource(
            typeof(AppSettings).Assembly,
            "SubMuxBatch.Core.Resources.lapse." + fileName);

    private static string ReadMkvToolNixNotices()
    {
        var assembly = typeof(AppSettings).Assembly;
        var resources = new[]
        {
            "SubMuxBatch.Core.Resources.mkvtoolnix.README.txt",
            "SubMuxBatch.Core.Resources.mkvtoolnix.licenses.pugixml-MIT.txt",
            "SubMuxBatch.Core.Resources.mkvtoolnix.licenses.nlohmann-json-MIT.txt",
            "SubMuxBatch.Core.Resources.mkvtoolnix.licenses.QtWaitingSpinner-MIT.txt",
            "SubMuxBatch.Core.Resources.mkvtoolnix.licenses.LGPL-3.0.txt",
            "SubMuxBatch.Core.Resources.mkvtoolnix.licenses.LGPL-2.1.txt",
            "SubMuxBatch.Core.Resources.mkvtoolnix.licenses.CC-BY-3.0.txt",
            "SubMuxBatch.Core.Resources.mkvtoolnix.licenses.Boost-1.0.txt"
        };

        return string.Join(
            "\n\n" + new string('=', 72) + "\n\n",
            resources.Select(resource => ReadResource(assembly, resource)));
    }

    private static string WithSourceNotice(string licenseText) =>
        $"Corresponding source archives and build details:\n{CorrespondingSourceUrl}\n\n{licenseText}";

    private sealed record LicenseEntry(string Title, string Text, string ProjectUrl);
}
