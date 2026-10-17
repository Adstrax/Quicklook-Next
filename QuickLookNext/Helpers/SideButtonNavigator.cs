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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

namespace QuickLookNext.Helpers;

/// <summary>
/// v6.0.0: serializes the mouse's side-button steps and folds a rapid burst into one net move.
/// Every request used to start from the same previewed file, so two quick presses could both step
/// to the same item. The anchor advances with each completed shell selection, while presses that
/// arrive during that work are added to the next step instead of racing it.
/// </summary>
internal sealed class SideButtonNavigator
{
    private readonly object _sync = new();
    private int _pendingDelta;
    private string _path = string.Empty;
    private bool _draining;

    /// <summary>
    /// Adds a step and reports whether this call is the one that has to start the drain task.
    /// An empty anchor or a zero step is ignored.
    /// </summary>
    internal bool Enqueue(int delta, string previewedPath, out bool startDrain)
    {
        startDrain = false;
        if (delta == 0 || string.IsNullOrEmpty(previewedPath))
            return false;

        lock (_sync)
        {
            if (!_draining)
            {
                _draining = true;
                _path = previewedPath;
                startDrain = true;
            }

            _pendingDelta += delta;
            return true;
        }
    }

    /// <summary>
    /// Takes the accumulated step and the file it starts from. Taking the last step leaves the
    /// navigator ready for a new burst; the next <see cref="Enqueue"/> starts a fresh drain.
    /// </summary>
    internal bool TryTake(out string path, out int delta)
    {
        lock (_sync)
        {
            path = _path;
            delta = _pendingDelta;

            if (delta == 0)
            {
                _draining = false;
                return false;
            }

            _pendingDelta = 0;
            return true;
        }
    }

    /// <summary>
    /// Moves the anchor to the item Explorer actually selected, so the next queued step starts
    /// where the previous one ended.
    /// </summary>
    internal void Advance(string selectedPath)
    {
        if (string.IsNullOrEmpty(selectedPath))
            return;

        lock (_sync)
            _path = selectedPath;
    }
}
