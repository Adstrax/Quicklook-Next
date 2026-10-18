// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.2: the caption template is applied lazily, and a theme change can rebuild
/// the application dictionaries in between. The viewer styles therefore carry the
/// icon font themselves; these tests make sure that local copy exists and really
/// resolves to the embedded font.
/// </summary>
internal class ViewerResourcesTests : ITestFixture
{
    // ResourceDictionary resolves "pack:" only once WPF has registered its URI
    // scheme, which normally happens when the Application object is created.
    private static Application _application;

    public void Setup()
    {
        _application ??= Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
    }

    public void Teardown()
    {
    }

    public void MainWindowStylesCarryTheIconFont()
    {
        var dictionary = Load("QuickLook.Common;component/Styles/MainWindowStyles.xaml");

        Assert.True(dictionary.Contains("SegoeFluent"), "the caption font is defined");
        Assert.True(
            dictionary["SegoeFluent"] is FontFamily,
            "the caption font is a FontFamily");
    }

    public void TheEmbeddedIconFontCanBeLoaded()
    {
        var dictionary = Load("QuickLook.Common;component/Styles/MainWindowStyles.xaml");
        var family = (FontFamily)dictionary["SegoeFluent"];

        Assert.True(
            family.Source.Contains("#Segoe Fluent Icons", StringComparison.Ordinal),
            "font name");
        Assert.True(family.GetTypefaces().GetEnumerator().MoveNext(), "at least one typeface");
    }

    public void CaptionButtonTemplateAppliesOnItsOwn()
    {
        var dictionary = Load("QuickLook.Common;component/Styles/MainWindowStyles.xaml");
        var button = new Button
        {
            Style = (Style)dictionary["CaptionButtonStyle"],
            Content = "\uE894",
        };

        // Measure applies the template. This is the path that used to throw
        // "Cannot find resource named 'SegoeFluent'" after a theme change.
        button.Measure(new Size(45, 32));
        button.Arrange(new Rect(0, 0, 45, 32));

        Assert.True(
            VisualTreeHelper.GetChildrenCount(button) > 0,
            "the caption template built its visual tree");
    }

    public void BusyIndicatorStylesCarryTheIconFont()
    {
        var dictionary = Load("QuickLook-Next;component/Controls/BusyDecorator/BusyDecorator.xaml");

        Assert.True(dictionary.Contains("SegoeFluent"), "the spinner font is defined");
    }

    private static ResourceDictionary Load(string relativeUri)
        => new()
        {
            Source = new Uri("pack://application:,,,/" + relativeUri, UriKind.Absolute),
        };
}
