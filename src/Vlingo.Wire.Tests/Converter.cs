// Copyright © 2012-2018 Vaughn Vernon. All rights reserved.
//
// This Source Code Form is subject to the terms of the
// Mozilla Public License, v. 2.0. If a copy of the MPL
// was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.

using System;
using System.IO;
using System.Text;
using Xunit.Abstractions;

namespace Vlingo.Wire.Tests
{
    public class Converter : TextWriter
    {
        ITestOutputHelper _output;
        
        public Converter(ITestOutputHelper output)
        {
            _output = output;
        }
        
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string message)
        {
            try
            {
                _output.WriteLine(message);
            }
            catch (InvalidOperationException e)
            {
                if (e.Message != "There is no currently active test.")
                {
                    throw;
                }
            }
        }

        public override void WriteLine(string format, params object[] args) => _output.WriteLine(format, args);
    }
}