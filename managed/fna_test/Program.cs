// FNA Switch Test — minimal rendering verification
//
// Draws a solid color that changes with controller input, plus a
// procedurally generated texture that moves across the screen.
//
// No asset files needed — the texture is generated in memory.
// This isolates the test to: mono interpreter → FNA → FNA3D → SDL2 → switch-mesa GL

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FnaNxTest;

public class FnaTestGame : Game
{
    private GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;
    private Texture2D _testTexture;
    private Vector2 _texturePos;
    private Vector2 _textureVel;
    private Color _bgColor;
    private int _frameCount;

    public FnaTestGame()
    {
        _graphics = new GraphicsDeviceManager(this);
        _graphics.PreferredBackBufferWidth = 1280;
        _graphics.PreferredBackBufferHeight = 720;
        _bgColor = new Color(30, 30, 60);
    }

    protected override void Initialize()
    {
        _texturePos = new Vector2(100, 100);
        _textureVel = new Vector2(3, 2);
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        // Generate a 64x64 checkerboard texture in memory — no file I/O needed
        _testTexture = new Texture2D(GraphicsDevice, 64, 64, false, SurfaceFormat.Color);
        uint[] pixels = new uint[64 * 64];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                bool checker = ((x / 8) + (y / 8)) % 2 == 0;
                if (checker)
                    pixels[y * 64 + x] = 0xFFFF6030; // ABGR: orange
                else
                    pixels[y * 64 + x] = 0xFF3060FF; // ABGR: blue
            }
        }
        _testTexture.SetData(pixels);
    }

    protected override void Update(GameTime gameTime)
    {
        var gamePad = GamePad.GetState(PlayerIndex.One);
        var keyboard = Keyboard.GetState();

        // Exit on B button (physical A on Switch — SDL2 maps to Xbox layout) or Escape
        if (gamePad.Buttons.B == ButtonState.Pressed ||
            keyboard.IsKeyDown(Keys.Escape))
        {
            Exit();
        }

        // Change background color with D-pad
        if (gamePad.DPad.Up == ButtonState.Pressed)
            _bgColor = new Color(60, 30, 30);
        if (gamePad.DPad.Down == ButtonState.Pressed)
            _bgColor = new Color(30, 30, 60);
        if (gamePad.DPad.Left == ButtonState.Pressed)
            _bgColor = new Color(30, 60, 30);
        if (gamePad.DPad.Right == ButtonState.Pressed)
            _bgColor = new Color(60, 60, 30);

        // Move texture with left stick. FNA returns Y positive=up, screen coords are Y down.
        Vector2 move = gamePad.ThumbSticks.Left;
        if (move.LengthSquared() > 0.01f)
        {
            _texturePos.X += move.X * 5;
            _texturePos.Y -= move.Y * 5;
        }
        else
        {
            // Auto-bounce if no stick input
            _texturePos += _textureVel;
        }

        // Bounce off screen edges
        if (_texturePos.X < 0 || _texturePos.X > 1280 - 64)
            _textureVel.X *= -1;
        if (_texturePos.Y < 0 || _texturePos.Y > 720 - 64)
            _textureVel.Y *= -1;
        _texturePos.X = MathHelper.Clamp(_texturePos.X, 0, 1280 - 64);
        _texturePos.Y = MathHelper.Clamp(_texturePos.Y, 0, 720 - 64);

        _frameCount++;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(_bgColor);

        _spriteBatch.Begin();
        _spriteBatch.Draw(_testTexture, _texturePos, Color.White);
        _spriteBatch.End();

        base.Draw(gameTime);
    }

    static void Main(string[] args)
    {
        using var game = new FnaTestGame();
        game.Run();
    }
}
