using JobsVssManager.Models;

namespace JobsVssManager.ViewModels
{
    public class JobViewModel : BaseViewModel
    {
        public JobModel Model { get; }
        public string Name => Model.Name;
        public string Path => Model.Path;

        public JobViewModel(JobModel model)
        {
            Model = model;
        }
    }
}
