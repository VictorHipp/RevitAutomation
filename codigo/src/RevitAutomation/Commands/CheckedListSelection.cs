using WF=System.Windows.Forms;

namespace RevitAutomation.Addin.Commands;

internal static class CheckedListSelection
{
    /// <summary>Mark only compatible entries and bring the first visible check into view.</summary>
    internal static void SelectAllCompatible(WF.CheckedListBox list,Func<object,bool> compatible)
    {
        int first=-1;
        list.BeginUpdate();
        try
        {
            for(int i=0;i<list.Items.Count;i++)
            {
                bool check=compatible(list.Items[i]);list.SetItemChecked(i,check);
                if(check&&first<0)first=i;
            }
            if(first>=0)list.TopIndex=first;
        }
        finally{list.EndUpdate();}
        list.Invalidate();
    }
}
