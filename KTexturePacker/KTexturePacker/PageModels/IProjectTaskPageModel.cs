using CommunityToolkit.Mvvm.Input;
using KTexturePacker.Models;

namespace KTexturePacker.PageModels
{
    public interface IProjectTaskPageModel
    {
        IAsyncRelayCommand<ProjectTask> NavigateToTaskCommand { get; }
        bool IsBusy { get; }
    }
}