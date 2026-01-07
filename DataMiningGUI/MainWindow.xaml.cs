using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DataBaseStructure;
using DataBaseStructure.AriaBase;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DataMiningGUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            List<string> jsonFiles = new List<string>();
            jsonFiles = AriaDataBaseJsonReader.ReturnPatientFileNames(@"C:\Users\BRA008\Modular_Projects\LocalDatabases\2025", jsonFiles, "*.json", SearchOption.AllDirectories);
            // Started doing these in 2020!
            List<PatientClass> allPatients = new List<PatientClass>();
            allPatients = AriaDataBaseJsonReader.ReadPatientFiles(jsonFiles);
        }
    }
}
