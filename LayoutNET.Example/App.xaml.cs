﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace LayoutNET.Example
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            //MessageBox.Show("Grid: " + TestAutoLayoutPanel.ArrangeMilliseconds);
            //MessageBox.Show("Panel: " + TestGrid.ArrangeMilliseconds);

            base.OnExit(e);
        }
    }
}
