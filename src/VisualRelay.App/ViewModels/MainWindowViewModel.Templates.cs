using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.Core.Tasks;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>Display names for the new-task Template dropdown; index-aligned
    /// with <see cref="_newTaskTemplates"/>.</summary>
    public ObservableCollection<string> NewTaskTemplateNames { get; } = [];

    [ObservableProperty]
    private int _selectedNewTaskTemplateIndex = -1;

    private IReadOnlyList<TaskTemplate> _newTaskTemplates = [];

    // What the last applied template put into each field. On template change a
    // field is overwritten only while empty or still equal to this, so browsing
    // templates never clobbers user-typed text.
    private string _lastAppliedTemplateTitle = string.Empty;
    private string _lastAppliedTemplateBody = string.Empty;

    /// <summary>Re-enumerates templates and applies the default (Blank). Called on
    /// every dialog open so template-file edits show up without watchers.</summary>
    private void PrepareNewTaskTemplates()
    {
        _newTaskTemplates = TaskTemplates.Load(
            TaskTemplates.ResolveUserTemplatesDir(EnvironmentAccessor),
            Path.Combine(RootPath, "llm-tasks", "templates"));

        NewTaskTemplateNames.Clear();
        foreach (var template in _newTaskTemplates)
        {
            NewTaskTemplateNames.Add(template.Name);
        }

        _lastAppliedTemplateTitle = string.Empty;
        _lastAppliedTemplateBody = string.Empty;

        // Reset to -1 first so assigning the default index below always fires the
        // change hook, even when the previous dialog session left the same index.
        SelectedNewTaskTemplateIndex = -1;
        SelectedNewTaskTemplateIndex = _newTaskTemplates.Count == 0 ? -1 : IndexOfBlank();
    }

    private int IndexOfBlank()
    {
        for (var i = 0; i < _newTaskTemplates.Count; i++)
        {
            if (string.Equals(_newTaskTemplates[i].Id, "blank", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    partial void OnSelectedNewTaskTemplateIndexChanged(int value)
    {
        if (value < 0 || value >= _newTaskTemplates.Count)
        {
            return;
        }

        var template = _newTaskTemplates[value];
        if (NewTaskTitle.Length == 0
            || string.Equals(NewTaskTitle, _lastAppliedTemplateTitle, StringComparison.Ordinal))
        {
            NewTaskTitle = template.Title;
        }
        if (NewTaskBody.Length == 0
            || string.Equals(NewTaskBody, _lastAppliedTemplateBody, StringComparison.Ordinal))
        {
            NewTaskBody = template.Body;
        }

        _lastAppliedTemplateTitle = template.Title;
        _lastAppliedTemplateBody = template.Body;
    }
}
