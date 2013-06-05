namespace Flux
{
    using System;
    using System.Runtime.Serialization;

    [Serializable]
    public sealed class InvalidRequestException : Exception
    {
        private readonly string _requestText;

        public InvalidRequestException(string requestText)
        {
            _requestText = requestText;
        }

        public InvalidRequestException(string requestText, string message)
            : base(message)
        {
            _requestText = requestText;
        }

#pragma warning disable 628
        protected InvalidRequestException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
            _requestText = info.GetString("RequestText");
        }
#pragma warning restore 628

        public string RequestText
        {
            get { return _requestText; }
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("RequestText", _requestText);
            base.GetObjectData(info, context);
        }
    }
}