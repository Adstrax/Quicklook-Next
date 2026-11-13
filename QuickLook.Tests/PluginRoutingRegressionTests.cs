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

using QuickLookNext;
using System;
using System.Linq;

namespace QuickLook.Tests;

internal class PluginRoutingRegressionTests
{
    public void EveryExtensionHintIsWellFormed()
    {
        var routing = PluginManager.GetExtensionRoutingSnapshot();

        Assert.True(routing.Count > 0, "the application declares lazy extension routes");
        foreach (var route in routing)
        {
            Assert.True(route.Key.StartsWith(".", StringComparison.Ordinal),
                $"{route.Key} starts with a dot");
            Assert.Equal(route.Key.ToLowerInvariant(), route.Key,
                $"{route.Key} is normalized to lower case");
            Assert.True(route.Value.Length > 0, $"{route.Key} has a plugin target");
            Assert.True(route.Value.All(name =>
                    name.StartsWith("QuickLook.Plugin.", StringComparison.Ordinal)),
                $"{route.Key} only targets QuickLook plugins");
        }
    }

    public void EveryExtensionHintTargetsAKnownLazyPlugin()
    {
        var routing = PluginManager.GetExtensionRoutingSnapshot();
        var lazyPlugins = PluginManager.GetLazyBuiltinPluginNamesSnapshot();

        foreach (var route in routing)
        {
            foreach (var plugin in route.Value)
            {
                Assert.True(lazyPlugins.Contains(plugin),
                    $"{route.Key} targets the known lazy plugin {plugin}");
            }
        }
    }
}
