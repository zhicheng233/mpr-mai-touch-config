import os
from xml.etree import ElementTree as ET


def generate_touch_svg(input_folder, output_folder):
    # Ensure the output folder exists
    os.makedirs(output_folder, exist_ok=True)

    # List all files in the input folder
    for filename in os.listdir(input_folder):
        if filename.endswith('.svg'):
            label = filename[:-4]  # Remove the .svg extension
            input_file_path = os.path.join(input_folder, filename)
            output_file_path = os.path.join(output_folder, f"{label}_touch.svg")

            # Parse the SVG file
            tree = ET.parse(input_file_path)
            root = tree.getroot()

            # Change the color of all paths to red for touch indication
            for elem in root.iter():
                if 'style' in elem.attrib:
                    style = elem.attrib['style']
                    style = style.replace('fill:', 'fill:#871818;')  # Change fill color to red
                    elem.attrib['style'] = style
                elif 'fill' in elem.attrib:
                    elem.attrib['fill'] = '#871818'  # Change fill color to red

            # Write the modified SVG to a new file
            tree.write(output_file_path)
            print(f"Generated {output_file_path}")


if __name__ == '__main__':
    input_folder = './'  # Folder containing original SVG files
    output_folder = './'  # Folder to save the generated touch SVG files

    generate_touch_svg(input_folder, output_folder)
