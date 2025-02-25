// Copyright © 2012-2018 Vaughn Vernon. All rights reserved.
//
// This Source Code Form is subject to the terms of the
// Mozilla Public License, v. 2.0. If a copy of the MPL
// was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.

using System.Net.Sockets;
using Vlingo.Wire.Message;

namespace Vlingo.Wire.Channel
{
    public abstract class SelectionReader
    {
        protected readonly ChannelMessageDispatcher Dispatcher;

        public SelectionReader(ChannelMessageDispatcher dispatcher)
        {
            Dispatcher = dispatcher;
        }

        public abstract void Read(Socket channel, RawMessageBuilder builder);

        protected void CloseClientResources(Socket socket)
        {
            socket.Close();
        }
    }
}