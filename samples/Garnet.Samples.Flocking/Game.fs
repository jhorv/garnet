﻿namespace Garnet.Samples.Flocking

open System
open System.Numerics
open System.Diagnostics
open System.Threading
open Veldrid
open Garnet.Resources
open Garnet.Samples.Engine
open Garnet.Composition
open Garnet.Samples.Flocking.Types

type UpdateTimer(fixedDeltaTime) =
    let mutable lastTime = 0L
    let mutable frameCount = 0L
    let sw = Stopwatch.StartNew()
    member c.Update() =
        let time = sw.ElapsedMilliseconds
        let result = { 
            frameNumber = frameCount
            time = time
            deltaTime = time - lastTime
            fixedDeltaTime = fixedDeltaTime
            fixedTime = time
            }
        frameCount <- frameCount + 1L
        lastTime <- time
        result

type Game(fs : IStreamSource) =
    let ren = new WindowRenderer { 
        CreateWindow.defaults with 
            title = "Flocking" 
            windowWidth = 800
            windowHeight = 600
        }
    let shaders = 
        fs.LoadShaderSet(ren.Device, 
            "texture-dual-color.vert", 
            "texture-dual-color.frag", 
            PositionTextureDualColorVertex.Description)
    let atlas = fs.LoadTextureAtlas(ren.Device, 512, 512, [ "hex.png"; "triangle.png" ])
    let layers = new SpriteRenderer(ren.Device, shaders, atlas.Texture, ren.Device.SwapchainFramebuffer.OutputDescription)
    do
        ren.Background <- RgbaFloat(0.0f, 0.1f, 0.2f, 1.0f) 
        ren.Add(layers)
    member _.Run() =
        // Create ECS container to hold game state and handle messages
        let c = Container.Create <| fun c ->
            Disposable.Create [
                c.AddCoreSystems()
                c.AddDrawingSystems()
                ]
        c.SetValue<TextureAtlas>(atlas)
        c.SetValue<SpriteRenderer>(layers)
        c.SetValue<WorldSettings>(WorldSettings.defaults)
        // Start loop
        c.Run(Start())
        let hud = FpsHud()
        let timer = UpdateTimer(16L)
        while ren.Update(0.0f) do
            // Call systems to update
            let e = timer.Update()
            hud.OnUpdate()
            c.Run<Update>(e)
            // Update transforms so origin is in the center of the screen and we use pixel coords
            // with +Y as up.
            let displayScale = 1.0f
            let size = ren.WindowSize.ToVector2() / displayScale
            let viewport = layers.GetViewport(0)
            viewport.ProjectionTransform <- Matrix4x4.CreateOrthographic(size.X, size.Y, -100.0f, 100.0f)
            // Call systems to draw
            c.Run(Draw())
            hud.Draw()
            // Draw to window
            ren.Invalidate()
            ren.Draw()
            // Sleep to avoid spinning CPU
            //Thread.Sleep(1)
    interface IDisposable with
        member c.Dispose() =
            atlas.Dispose()
            shaders.Dispose()
            ren.Dispose()
    static member Run(fs) =
        use game = new Game(fs)
        game.Run()
