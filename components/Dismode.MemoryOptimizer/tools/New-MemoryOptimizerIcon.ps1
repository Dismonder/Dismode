# SPDX-License-Identifier: GPL-3.0-only

[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) `
        "src\Dismode.MemoryOptimizer\Assets\MemoryOptimizer.ico")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF] $Rectangle,
        [float] $Radius
    )

    $diameter = 2 * $Radius
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc(
        $Rectangle.Right - $diameter,
        $Rectangle.Y,
        $diameter,
        $diameter,
        270,
        90)
    $path.AddArc(
        $Rectangle.Right - $diameter,
        $Rectangle.Bottom - $diameter,
        $diameter,
        $diameter,
        0,
        90)
    $path.AddArc(
        $Rectangle.X,
        $Rectangle.Bottom - $diameter,
        $diameter,
        $diameter,
        90,
        90)
    $path.CloseFigure()
    return $path
}

function New-IconFrame {
    param([int] $Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $margin = [Math]::Max(1.0, $Size * 0.055)
        $tile = [System.Drawing.RectangleF]::new(
            [float]$margin,
            [float]$margin,
            [float]($Size - (2 * $margin)),
            [float]($Size - (2 * $margin)))
        $tilePath = New-RoundedPath $tile ([float]($Size * 0.20))
        try {
            $tileBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $tile,
                [System.Drawing.Color]::FromArgb(255, 18, 29, 38),
                [System.Drawing.Color]::FromArgb(255, 5, 10, 15),
                135.0)
            try {
                $graphics.FillPath($tileBrush, $tilePath)
            }
            finally {
                $tileBrush.Dispose()
            }

            $borderWidth = [Math]::Max(1.0, $Size * 0.025)
            $border = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(255, 42, 70, 82),
                [float]$borderWidth)
            try {
                $graphics.DrawPath($border, $tilePath)
            }
            finally {
                $border.Dispose()
            }
        }
        finally {
            $tilePath.Dispose()
        }

        $chip = [System.Drawing.RectangleF]::new(
            [float]($Size * 0.23),
            [float]($Size * 0.27),
            [float]($Size * 0.54),
            [float]($Size * 0.46))
        $chipPath = New-RoundedPath $chip ([float]($Size * 0.075))
        try {
            $chipBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $chip,
                [System.Drawing.Color]::FromArgb(255, 38, 234, 218),
                [System.Drawing.Color]::FromArgb(255, 15, 168, 205),
                30.0)
            try {
                $graphics.FillPath($chipBrush, $chipPath)
            }
            finally {
                $chipBrush.Dispose()
            }
        }
        finally {
            $chipPath.Dispose()
        }

        $slotPen = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(220, 4, 25, 31),
            [float][Math]::Max(1.0, $Size * 0.045))
        try {
            $slotPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $slotPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            foreach ($y in @(0.40, 0.52, 0.64)) {
                $graphics.DrawLine(
                    $slotPen,
                    [float]($Size * 0.35),
                    [float]($Size * $y),
                    [float]($Size * 0.65),
                    [float]($Size * $y))
            }
        }
        finally {
            $slotPen.Dispose()
        }

        $pinPen = [System.Drawing.Pen]::new(
            [System.Drawing.Color]::FromArgb(255, 32, 215, 208),
            [float][Math]::Max(1.0, $Size * 0.035))
        try {
            foreach ($position in @(0.34, 0.50, 0.66)) {
                $graphics.DrawLine(
                    $pinPen,
                    [float]($Size * $position),
                    [float]($Size * 0.19),
                    [float]($Size * $position),
                    [float]($Size * 0.27))
                $graphics.DrawLine(
                    $pinPen,
                    [float]($Size * $position),
                    [float]($Size * 0.73),
                    [float]($Size * $position),
                    [float]($Size * 0.81))
            }
        }
        finally {
            $pinPen.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return $stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$frames = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    [pscustomobject]@{
        Size = $size
        Data = New-IconFrame $size
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$file = [System.IO.FileStream]::new(
    $resolvedOutput,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Data)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "Generated $resolvedOutput"
