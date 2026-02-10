using System.IO;

namespace WWiseToolsWPF.Classes.BankClasses.Chunks.HircSections
{
    public class StateHircSection : IdedHircSection
    {
        public override void Read(BinaryReader reader)
        {
            base.Read(reader);

            // CAkState::SetInitialValues
        }
    }
}
