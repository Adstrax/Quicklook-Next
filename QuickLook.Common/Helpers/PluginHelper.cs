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
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using QuickLook.Common.Plugin;

namespace QuickLook.Common.Helpers;

public static class PluginHelper
{
    public static void RunAndClosePreview()
    {
        GetInstance()?.RunAndClosePreview();
    }

    public static void InvokePreview(string path = null)
    {
        GetInstance()?.InvokePreview(path);
    }

    public static void InvokePreviewWithOption(string path = null, string options = null)
    {
        GetInstance()?.InvokePreviewWithOption(path, options);
    }

    public static void InvokePluginPreview(string plugin, string path = null)
    {
        GetInstance()?.InvokePluginPreview(plugin, path);
    }

    /// <summary>
    /// Starts a host-owned preview that can be placed inside another WPF panel.
    /// The caller must dispose the returned session when it is no longer shown.
    /// </summary>
    public static IEmbeddedPreviewSession CreateEmbeddedPreview(string path, object source = null)
    {
        try
        {
            dynamic instance = GetInstance();
            return instance?.CreateEmbeddedPreview(path, source) as IEmbeddedPreviewSession;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return null;
        }
    }

    private static dynamic GetInstance()
    {
        try
        {
            // Obtain the instance from QuickLookNext::ViewWindowManager.GetInstance()
            Type type = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(a => a.GetType("QuickLookNext.ViewWindowManager", throwOnError: false))
                .FirstOrDefault(t => t != null);

            if (type == null)
            {
                return null;
            }

            MethodInfo method = type.GetMethod("GetInstance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (method == null)
            {
                return null;
            }

            return method.Invoke(null, null);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        return null;
    }
}
