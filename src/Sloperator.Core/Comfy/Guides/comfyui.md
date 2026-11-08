# Using ComfyUI (Sloperator's guide)

## Workflows
- A workflow is JSON in ComfyUI's API format: {"node id": {"class_type": "…", "inputs": {"name": a
  value, or ["other node id", output index]}}}.
- Never write node, input or model names from memory: start from the template closest to the
  request (action=templates with a few words in search finds them; with name it shows one as a
  workflow, saved to a file you can edit), or look names up (action=nodes with search or names,
  action=models).
- For what no template does, first read how the model's makers do it (their guide and example
  workflows, with web_search), then build the workflow from node and model names you looked up.
- action=run runs a workflow (template, workflow_file, or workflow itself), changed by set
  {"node id.input": value}, e.g. {"6.text": "a red fox in snow", "3.seed": 42}; it is checked first,
  then it waits until the job is done and gives you the files with a look at them. Look at what it
  made before you answer. Change the seed for a different result.
- For an input picture, sound or video, put its full local path where LoadImage/LoadAudio/LoadVideo
  name a file: it is uploaded.

## A picture to follow or change
- A picture the user gives you to follow (a character, a reference sheet, a style) is where the
  result starts, not something to describe in words: image to image from it
  (sloperator/image_to_image; denoise 0.5 to 0.7 keeps the character), or a reference or edit
  model's template when one is installed. Crop or scale it inside the workflow (ImageCrop,
  ImageScaleToTotalPixels), not with a script.
- To change a picture or restyle it (put a hat on, make a photo anime or a painting), or to draw the
  character it shows in a new scene or pose, an installed edit model's template (templates search
  "edit": Qwen-Image-Edit, Flux Kontext) does what is asked and keeps the subject, with no download;
  image to image keeps the photo's look at low denoise and loses the subject at high.

## Video and sound
- A video starts from a picture: make it with an image model first and look at it, then animate it
  with an image-to-video template (first and last frames when where it ends matters); text-to-video
  draws its own picture badly, so only when the user asks for it.
- LTX makes the video's sound (speech, footsteps, wind) but not music: say "no music" and make music
  with a music model (ACE-Step). Foley for a video that has no sound is sloperator/video_to_audio
  (MMAudio).
- Before writing a video prompt, read action=guide search="video": motion, direction, poses and
  props are what ruin most takes.

## Models
- A checkpoint's or LoRA's card (with action=models search=its file name, a template's view, or
  after its first run) says how its maker uses it: the prompt style, size, sampler, CFG and negative
  prompt to start from.
- A model a workflow lacks: action=download with its url (a template lists the links of its models).
  A custom node pack or program it lacks you can install yourself (then action=restart).
- action=find_models searches Civitai and Hugging Face for others (a style, a checkpoint the user
  named, a LoRA, an upscaler) with their sizes and links, and what the month's most downloaded on
  Civitai are for those words; without search it lists this month's most downloaded checkpoints
  (kind=lora: LoRAs) and the base models they are on, so you know what is current before you pick one.

## What it makes
- A run's files stay in ComfyUI's output folder, where the user finds them; copy them elsewhere
  (save_to) only when the user names a place. Give their full paths in your answer.

## The server
- The user's own ComfyUI is found and used (one already running, a portable build, the Desktop app,
  an install of their own), started for you if needed; only when the PC has none, action=install
  installs one (the portable build that suits its graphics card, or its CPU).
- action=status shows the server, GPU and queue (with prompt_id: waits for that job); action=cancel
  with prompt_id stops that job only; action=free unloads its models to free the GPU;
  action=restart restarts it (after installing custom nodes into its custom_nodes folder);
  action=stop stops it.
