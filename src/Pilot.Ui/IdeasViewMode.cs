using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The Ideas pane's views of <see cref="IdeasBoard"/>: the list, the tree grid and the canvas, picked on the one <see cref="ViewSwitch"/>.</summary>
internal enum IdeasViewMode
{
    List,
    Tree,
    Canvas,
}
