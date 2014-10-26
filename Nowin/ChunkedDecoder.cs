using System;
using System.Diagnostics;

namespace Nowin
{
    internal struct ChunkedDecoder
    {
        long _dataAvail;
        long _nextAvail;
        enum State
        {
            InHex,
            AfterHex,
            AfterCR,
            InData,
            AfterCR2,
            AfterLF2,
            AfterCR3,
            End,
            InTrailer,
            AfterTrailerCR
        }
        State _state;

        public void Reset()
        {
            _dataAvail = 0;
            _nextAvail = 0;
            _state = State.InHex;
        }

        public int DataAvailable
        {
            get
            {
                if (_dataAvail > int.MaxValue) return int.MaxValue;
                return (int)_dataAvail;
            }
        }

        public bool ProcessByte(byte b)
        {
            switch (_state)
            {
                case State.InHex:
                    var h = Transport2HttpHandler.ParseHexChar(b);
                    if (h < 0)
                    {
                        _state = State.AfterHex;
                        goto case State.AfterHex;
                    }
                    _nextAvail = _nextAvail * 16 + h;
                    break;
                case State.AfterHex:
                    if (b == 13)
                    {
                        _state = State.AfterCR;
                    }
                    break;
                case State.AfterCR:
                    if (b == 10)
                    {
                        _state = State.InData;
                        _dataAvail = _nextAvail;
                        break;
                    }
                    _state = State.AfterHex;
                    break;
                case State.InData:
                    if (b==13)
                        _state = State.AfterCR2;
                    else
                    {
                        Debug.Assert(_nextAvail == 0);
                        _state = State.InTrailer;
                    }
                    break;
                case State.AfterCR2:
                    Debug.Assert(b == 10);
                    if (_nextAvail == 0)
                    {
                        _state = State.End;
                        _dataAvail = -1;
                        return true;
                    }
                    _state = State.InHex;
                    _nextAvail = 0;
                    break;
                case State.AfterLF2:
                    if (b == 13)
                    {
                        _state = State.AfterCR3;
                    }
                    else
                    {
                        _state = State.InTrailer;
                    }
                    break;
                case State.InTrailer:
                    if (b == 13)
                    {
                        _state = State.AfterTrailerCR;
                    }
                    break;
                case State.AfterTrailerCR:
                    Debug.Assert(b == 10);
                    _state = State.AfterLF2;
                    break;
                case State.AfterCR3:
                    Debug.Assert(b == 10);
                    _state = State.End;
                    _dataAvail = -1;
                    return true;
                default:
                    throw new InvalidOperationException();
            }
            return false;
        }

        public void DataEatten(int count)
        {
            _dataAvail -= count;
        }
    }
}