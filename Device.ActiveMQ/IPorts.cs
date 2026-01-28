namespace Device.ActiveMQ
{

    public interface IPort : IDisposable
    {

  
   
    }


    public interface IAdapterPort : IPort
    {
        // Synchronous Methods
        void Write(string message, string newQueue, bool fireEvent );
   
        string Read(string newQueue);

        // Asynchronous Methods
        Task WriteAsync(string message, string newQueue , bool fireEvent);
        //void ReadAsync(string newQueue);
        //void ReadAsync(Func<string> function);

        public void ReadNotify(Action<string,string> onMessage);

        public void ReadNotify(Action<string,string> onMessage, string strQueue);

        public void ReadNotifyEnd();

        public void ReadNotifyEnd(string strQueue);

        public void Disconnect();

    }


}