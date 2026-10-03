using System;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using SDL2;

namespace Terraria.NxInputDiag;

public static class InputDiagnostics
{
    public static void InstallSwitchMapping()
    {
        const string mapping = "000038f853776974636820436f6e7400,Switch Controller,a:b0,b:b1,back:b11,dpdown:b15,dpleft:b12,dpright:b14,dpup:b13,leftshoulder:b6,leftstick:b4,lefttrigger:b8,leftx:a0,lefty:a1,rightshoulder:b7,rightstick:b5,righttrigger:b9,rightx:a2,righty:a3,start:b10,x:b2,y:b3,";
        int result = SDL.SDL_GameControllerAddMapping(mapping);
        FNALoggerEXT.LogInfo("NX Switch mapping install result=" + result.ToString());
    }

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void BeginTick();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void EndTick();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void BeginUpdate();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void EndUpdate();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void BeginDraw();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern void EndDraw();

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern byte GetButton(IntPtr controller, int button);
}
