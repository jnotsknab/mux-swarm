# Agent Prompt Templates

This file contains reusable prompt templates for the AI Prompt Optimizer skill.

## CodeAgent Prompts

### Basic Optimization Template

```
You are a prompt engineering expert. Optimize the following prompt to produce 
better responses from LLMs.

Original Prompt:
{prompt}

Analyze and improve it by:
1. Adding clarity to the objective
2. Specifying output format
3. Defining persona/tone
4. Adding context or background
5. Including constraints or limits
6. Adding examples if helpful

Output format:
## Optimized Prompt
[Your optimized version here]

## Explanation
- What changed: [list changes]
- Why it helps: [explain improvement]
- Score improvement: [before/after assessment]
```

### Technical Documentation Template

```
Create an optimized prompt for generating technical documentation.

Task: {documentation_type}
Target audience: {audience}
Technology: {technology}

Generate a prompt template with these placeholders:
- [TOPIC] - what to document
- [COMPLEXITY] - beginner/intermediate/advanced
- [FORMAT] - markdown/html/pdf
- [LENGTH] - brief/detailed/comprehensive

Include requirements for:
- Code examples
- API references
- Usage instructions
- Troubleshooting tips
```

### Creative Writing Template

```
Create an optimized prompt for creative writing tasks.

Genre: {genre}
Tone: {tone}
Length: {length}
 POV: {point_of_view}

Your template should include:
- Story premise structure
- Character development requirements
- Setting description
- Plot arc guidance
- Dialogue style guidelines
- Prose style preferences
```

### Coding Assistance Template

```
Create an optimized prompt for coding assistance.

Language/Framework: {language}
Experience Level: {level}
Task Type: {task_type}

Include:
- Persona definition (senior dev, teaching assistant, etc.)
- Code context requirements
- Input/output specifications
- Error handling requirements
- Test case expectations
- Documentation needs
```

### Research Template

```
Create an optimized prompt for research tasks.

Topic: {topic}
Depth: {depth}
Sources: {source_type}

Include requirements for:
- Information scope
- Source recency
- Credibility criteria
- Comparison needs
- Output structure
- Citation style
```

### Data Analysis Template

```
Create an optimized prompt for data analysis tasks.

Data Type: {data_type}
Analysis Goal: {goal}
Tools: {tools}

Include:
- Data description requirements
- Analysis methodology
- Visualization requirements
- Statistical measures needed
- Output format (tables, charts, summary)
- Interpretation guidance
```

## MemoryAgent Prompts

### Store Optimized Prompt

```
Store the following optimized prompt in the library:

Name: {prompt_name}
Category: {category}
Original Prompt: {original}
Optimized Prompt: {optimized}
Use Case: {use_case}
Tags: {tags}
Created: {timestamp}
```

### Retrieve Similar Prompts

```
Search the prompt library for:
- Category: {category}
- Tags: {tags}
- Use case: {use_case}

Return matching prompts with their metadata and usage contexts.
```

### Update Prompt Usage

```
Update usage statistics for prompt: {prompt_key}
- Increment use count
- Add new use case: {use_case}
- Note any modifications made
```

## Optimization Analysis Prompts

### Weakness Detection

```
Analyze this prompt for common weaknesses:

{prompt}

Rate each category 1-10 and identify specific issues:
1. Clarity - is the goal unambiguous?
2. Specificity - are requirements detailed?
3. Structure - is output format defined?
4. Context - is background provided?
5. Constraints - are limits stated?
6. Persona - is tone/voice specified?
7. Examples - are samples included?

Provide specific recommendations for each low-scoring area.
```

### Iterative Refinement

```
This prompt was used but produced suboptimal results:

Prompt: {prompt}
Issue: {problem_description}

Suggest modifications to address:
1. The specific issue reported
2. Related weaknesses that may contribute
3. Potential edge cases to handle

Provide an improved version with explanation.
```

## Template Variables

Common placeholders for templates:

| Placeholder | Description | Example |
|-------------|-------------|---------|
| {prompt} | The original user prompt | "Write about Python" |
| {task_type} | Type of task | coding, writing, research |
| {audience} | Target readers | beginners, experts |
| {topic} | Subject matter | Docker, Python, React |
| {format} | Output format | markdown, json, html |
| {length} | Desired length | brief, detailed |
| {tone} | Writing tone | formal, casual, technical |
| {persona} | Role to assume | expert, teacher, critic |
| {constraints} | Limitations | max 500 words, no jargon |
| {examples} | Sample inputs/outputs | See attached |

## Quick Reference

### Trigger Words → Agent Action

| User Says... | Use Agent | Action |
|--------------|-----------|--------|
| "improve this" | CodeAgent | optimize |
| "analyze my prompt" | CodeAgent | analyze |
| "make a template" | CodeAgent | template |
| "save this" | MemoryAgent | store |
| "find similar" | MemoryAgent | retrieve |
